using BencodeNET.Torrents;

using Bi2pTorrent.Client.Extensions;
using Bi2pTorrent.Client.Protocol;

using DotI2p;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Bi2pTorrent.Client;

public class PeerConnection(SamSession samSession, string myPeerId, Torrent torrent, Peer peer, IPeerEventHandler eventHandler)
{
    private const int MaxBacklog = 10;
    private const int BlockSize = 16 * 1024;
    private TcpClient? tcpClient;
    private Bitfield bitfield = new Bitfield(torrent.NumberOfPieces);
    private readonly AutoResetEvent messageQueueEvent = new AutoResetEvent(false);
    private readonly ConcurrentQueue<IMessage> messageQueue = [];
    private readonly System.Timers.Timer heartbeatTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30));
    private readonly System.Timers.Timer statsTimer = new System.Timers.Timer(TimeSpan.FromSeconds(10));
    private readonly AutoResetEvent downloadQueueEvent = new AutoResetEvent(false);
    private readonly ConcurrentQueue<int> downloadQueue = [];
    private readonly Dictionary<int, MemoryPiece> downloadMemory = [];
    private readonly Dictionary<int, MemoryPiece> uploadMemory = [];
    private readonly SemaphoreSlim backlogSemaphore = new SemaphoreSlim(MaxBacklog);
    private readonly object statsLock = new object();
    private ulong bytesRead = 0;
    private ulong lastBytesRead = 0;
    private ulong bytesSent = 0;
    private ulong lastBytesSent = 0;

    public bool RemoteChoked { get; private set; } = true;

    public bool LocalChoked { get; private set; } = true;

    public bool RemoteInterested { get; private set; } = false;

    public bool LocalInterested { get; private set; } = false;

    public Bitfield Bitfield => this.bitfield;

    public Peer Peer => peer;

    public int[] BusyPieces
    {
        get
        {
            lock (this.downloadMemory)
            {
                lock (this.downloadQueue)
                {
                    return downloadMemory.Keys.Concat(this.downloadQueue).ToArray();
                }
            }
        }
    }

    public async Task<bool> ConnectAsync(TcpClient? tcpClient, Handshake? receiveHandshake = null)
    {
        this.tcpClient = tcpClient;
        this.tcpClient!.ReceiveTimeout = 20000;
        var stream = this.tcpClient!.GetStream();

        var infoHash = torrent.GetInfoHashBytes();

        var sendHandshake = new Handshake(infoHash, myPeerId);
        await sendHandshake.ToStreamAsync(stream);
        await stream.FlushAsync();

        if (receiveHandshake == null)
        {
            try
            {
                receiveHandshake = await Handshake.FromStreamAsync(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{peer.Address} - Handshake failed: {ex.Message}");
                return false;
            }
        }

        if (receiveHandshake.Length != 19 || receiveHandshake.Protocol != "BitTorrent protocol")
        {
            Console.WriteLine($"{peer.Address} - Handshake failed: Invalid protocol identifier.");
            return false;
        }

        if (!receiveHandshake.InfoHash.SequenceEqual(infoHash))
        {
            Console.WriteLine($"{peer.Address} - Handshake failed: InfoHash does not match.");
            return false;
        }

        Console.WriteLine($"{peer.Address} - Handshake successful! Extensions: {string.Join(' ', receiveHandshake.Reserved.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')))}");

        _ = Task.Factory.StartNew(ReceiverAsync, TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(SenderAsync, TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(DownloadManagerAsync, TaskCreationOptions.LongRunning);

        this.heartbeatTimer.Elapsed += (s, e) =>
        {
            this.messageQueue.Enqueue(new KeepAliveMessage());
            this.messageQueueEvent.Set();
        };
        this.heartbeatTimer.Start();

        this.statsTimer.Elapsed += (s, e) =>
        {
            lock (this.statsLock)
            {
                Console.WriteLine($"{peer.Address}: up: {(bytesSent - lastBytesSent) / 1024.0 / 10.0:N1} kbit/s, down: {(bytesRead - lastBytesRead) / 1024.0 / 10.0:N1} kbit/s");
                this.lastBytesSent = this.bytesSent;
                this.lastBytesRead = this.bytesRead;
            }
        };
        this.statsTimer.Start();

        return true;
    }

    public void SetChoked(bool choked)
    {
        if (this.LocalChoked == choked)
        {
            return;
        }

        this.LocalChoked = choked;
        this.messageQueue.Enqueue(choked ? new ChokeMessage() : new UnchokeMessage());
        this.messageQueueEvent.Set();
    }

    public void SetInterested(bool interested)
    {
        if (this.LocalInterested == interested)
        {
            return;
        }

        this.LocalInterested = interested;
        this.messageQueue.Enqueue(interested ? new InterestedMessage() : new NotInterestedMessage());
        this.messageQueueEvent.Set();
    }

    public void SendHave(int pieceIndex)
    {
        this.messageQueue.Enqueue(new HaveMessage { PieceIndex = pieceIndex });
        this.messageQueueEvent.Set();

        lock (this.downloadMemory)
        {
            this.downloadMemory.Remove(pieceIndex);
        }
    }

    public void SendBitfield(Bitfield bitfield)
    {
        this.messageQueue.Enqueue(new BitfieldMessage(bitfield.Bytes));
        this.messageQueueEvent.Set();
    }

    public void AssignDownloadPiece(int pieceIndex)
    {
        this.downloadQueue.Enqueue(pieceIndex);
        this.downloadQueueEvent.Set();
    }

    private async Task ReceiverAsync()
    {
        this.tcpClient!.ReceiveTimeout = 180000;    // 3 minutes
        var stream = this.tcpClient!.GetStream();
        Memory<byte> buffer = new byte[32 * 1024];
        var lengthBytes = new byte[4];

        while (this.tcpClient.Connected)
        {
            await stream.ReadExactlyAsync(lengthBytes);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

            if (length == 0)
            {
                // Heartbeat message, ignore
                Console.WriteLine($"{peer.Address} -> Heartbeat");
                continue;
            }

            if (length > 32 * 1024)
            {
                Console.WriteLine($"{peer.Address} - Invalid message length {length}, closing connection.");
                this.tcpClient.Close();
                break;
            }

            var bufferSlice = buffer.Slice(0, length);
            await stream.ReadExactlyAsync(bufferSlice);

            lock (statsLock)
            {
                bytesRead += 4 + (ulong)length;
            }

            var message = MessageFactory.Create(bufferSlice);

            if (message is ChokeMessage chokeMessage)
            {
                Console.WriteLine($"{peer.Address} -> Choke");

                if (!this.RemoteChoked)
                {
                    this.RemoteChoked = true;

                    lock (this.downloadQueue)
                    {
                        this.downloadQueue.Clear();
                    }

                    lock (this.downloadMemory)
                    {
                        this.downloadMemory.Clear();
                    }

                    this.backlogSemaphore.Release(MaxBacklog - this.backlogSemaphore.CurrentCount);
                    eventHandler.RemoteChokedChanged(this, true);
                }
            }
            else if (message is UnchokeMessage unchokeMessage)
            {
                Console.WriteLine($"{peer.Address} -> Unchoke");

                if (this.RemoteChoked)
                {
                    this.RemoteChoked = false;
                    eventHandler.RemoteChokedChanged(this, false);
                }
            }
            else if (message is InterestedMessage interestedMessage)
            {
                Console.WriteLine($"{peer.Address} -> Interested");

                if (!this.RemoteInterested)
                {
                    this.RemoteInterested = true;
                    eventHandler.RemoteInterestedChanged(this, true);
                }
            }
            else if (message is NotInterestedMessage notInterestedMessage)
            {
                Console.WriteLine($"{peer.Address} -> Not Interested");

                if (this.RemoteInterested)
                {
                    lock (this.uploadMemory)
                    {
                        this.uploadMemory.Clear();
                    }

                    this.RemoteInterested = false;
                    eventHandler.RemoteInterestedChanged(this, false);
                }
            }
            else if (message is HaveMessage haveMessage)
            {
                Console.WriteLine($"{peer.Address} -> Have {haveMessage.PieceIndex}");

                this.bitfield.SetPiece(haveMessage.PieceIndex);
                eventHandler.BitfieldChanged(this, this.bitfield);

                lock (this.uploadMemory)
                {
                    if (this.uploadMemory.ContainsKey(haveMessage.PieceIndex))
                    {
                        this.uploadMemory.Remove(haveMessage.PieceIndex);
                    }
                }
            }
            else if (message is BitfieldMessage bitfieldMessage)
            {
                this.bitfield = new Bitfield(torrent.NumberOfPieces, bitfieldMessage.Bitfield);

                Console.WriteLine($"{peer.Address} -> Bitfield ({bitfieldMessage.Bitfield.Length} bytes), has {this.bitfield.CompletedPieceCount} of {torrent.NumberOfPieces} pieces = {this.bitfield.CompletedPieceCount * 100.0 / torrent.NumberOfPieces:N1}%");

                eventHandler.BitfieldChanged(this, this.bitfield);
            }
            else if (message is RequestMessage requestMessage)
            {
                var pieceMessage = new PieceMessage(
                    requestMessage.PieceIndex,
                    requestMessage.Begin,
                    requestMessage.Length);

                bool loadPiece = false;

                lock (this.uploadMemory)
                {
                    loadPiece = !this.uploadMemory.ContainsKey(pieceMessage.PieceIndex);
                }

                var memoryPiece = await eventHandler.LoadPieceAsync(this, pieceMessage.PieceIndex);

                lock (this.uploadMemory)
                {
                    this.uploadMemory[pieceMessage.PieceIndex] = memoryPiece;
                }

                this.messageQueue.Enqueue(pieceMessage);
                this.messageQueueEvent.Set();
            }
            else if (message is PieceMessage pieceMessage)
            {
                this.backlogSemaphore.Release();
                var complete = false;

                lock (this.downloadMemory)
                {
                    if (this.downloadMemory.ContainsKey(pieceMessage.PieceIndex))
                    {
                        this.downloadMemory[pieceMessage.PieceIndex].Write(pieceMessage.GetData(), pieceMessage.Begin);
                        complete = this.downloadMemory[pieceMessage.PieceIndex].IsComplete();
                    }
                }

                if (complete)
                {
                    Console.WriteLine($"{peer.Address} -> Received piece {pieceMessage.PieceIndex}");

                    await eventHandler.ReceivedPieceAsync(this, this.downloadMemory[pieceMessage.PieceIndex]);

                    lock (this.downloadMemory)
                    {
                        this.downloadMemory.Remove(pieceMessage.PieceIndex);
                    }
                }
            }
            else if (message is CancelMessage cancelMessage)
            {
                Console.WriteLine($"{peer.Address} -> Cancel piece {cancelMessage.PieceIndex}, begin {cancelMessage.Begin}, length {cancelMessage.Length}");

                foreach (var item in this.messageQueue)
                {
                    if (item is PieceMessage pieceMsg &&
                        pieceMsg.PieceIndex == cancelMessage.PieceIndex && pieceMsg.Begin == cancelMessage.Begin && pieceMsg.Length == cancelMessage.Length)
                    {
                        pieceMsg.Cancelled = true;
                    }
                }
            }
            else
            {
                Console.WriteLine($"{peer.Address} -> Unknown message type {message}");
            }
        }
    }

    private async Task SenderAsync()
    {
        var stream = this.tcpClient!.GetStream();
        Memory<byte> sendBuffer = new byte[64];

        while (this.tcpClient!.Connected)
        {
            this.messageQueueEvent.WaitOne();

            while (this.tcpClient!.Connected && this.messageQueue.TryDequeue(out var message))
            {
                int bytesWritten = 0;

                if (message is ChokeMessage or UnchokeMessage or InterestedMessage or NotInterestedMessage)
                {
                    var buffer = sendBuffer.Slice(0, 5);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 1);
                    buffer.Span[4] = message.Type;
                    await stream.WriteAsync(buffer);

                    Console.WriteLine($"{peer.Address} <- {message.GetType().Name}");
                    bytesWritten += buffer.Length;
                }
                else if (message is BitfieldMessage bitfieldMessage)
                {
                    var buffer = sendBuffer.Slice(0, 5);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, bitfieldMessage.Bitfield.Length + 1);
                    buffer.Span[4] = bitfieldMessage.Type;
                    await stream.WriteAsync(buffer);
                    await stream.WriteAsync(bitfieldMessage.Bitfield);

                    Console.WriteLine($"{peer.Address} <- Bitfield");
                    bytesWritten += buffer.Length + bitfieldMessage.Bitfield.Length;
                }
                else if (message is HaveMessage haveMessage)
                {
                    var buffer = sendBuffer.Slice(0, 9);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 5);
                    buffer.Span[4] = haveMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5, 4).Span, haveMessage.PieceIndex);
                    await stream.WriteAsync(buffer);

                    Console.WriteLine($"{peer.Address} <- Have {haveMessage.PieceIndex}");
                    bytesWritten += buffer.Length;
                }
                else if (message is KeepAliveMessage)
                {
                    var buffer = sendBuffer.Slice(0, 4);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 0);
                    await stream.WriteAsync(buffer);

                    Console.WriteLine($"{peer.Address} <- Heartbeat");
                    bytesWritten += buffer.Length;
                }
                else if (message is PieceMessage pieceMessage && !pieceMessage.Cancelled)
                {
                    var buffer = sendBuffer.Slice(0, 13); ;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 9 + pieceMessage.Length);
                    buffer.Span[4] = pieceMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5).Span, pieceMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(9).Span, pieceMessage.Begin);
                    await stream.WriteAsync(buffer);

                    ReadOnlyMemory<byte> data;

                    lock (this.uploadMemory)
                    {
                        data = this.uploadMemory[pieceMessage.PieceIndex].Data.Slice(pieceMessage.Begin, pieceMessage.Length);
                    }

                    await stream.WriteAsync(data);
                    bytesWritten += buffer.Length + data.Length;
                }
                else if (message is RequestMessage requestMessage && !this.RemoteChoked)
                {
                    var buffer = sendBuffer.Slice(0, 17);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 13);
                    buffer.Span[4] = requestMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5).Span, requestMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(9).Span, requestMessage.Begin);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(13).Span, requestMessage.Length);
                    await stream.WriteAsync(buffer);

                    bytesWritten += buffer.Length;
                }

                lock (this.statsLock)
                {
                    this.bytesSent += (ulong)bytesWritten;
                }
            }

            await stream.FlushAsync();
        }
    }

    private async Task DownloadManagerAsync()
    {
        while (true)
        {
            this.downloadQueueEvent.WaitOne();

            while (this.downloadQueue.TryDequeue(out var pieceIndex))
            {
                Console.WriteLine($"Starting download of piece {pieceIndex}");

                // Last piece is of irregular length
                var pieceSize = torrent.GetPieceSize(pieceIndex);

                lock (this.downloadMemory)
                {
                    this.downloadMemory[pieceIndex] = new MemoryPiece(pieceIndex, pieceSize, new byte[pieceSize]);
                }

                for (int offset = 0; offset < pieceSize; offset += BlockSize)
                {
                    int blockSize = (int)Math.Min(BlockSize, pieceSize - offset);

                    await this.backlogSemaphore.WaitAsync();

                    if (this.RemoteChoked)
                    {
                        this.backlogSemaphore.Release();
                        break;
                    }

                    this.messageQueue.Enqueue(new RequestMessage(pieceIndex, offset, blockSize));
                    this.messageQueueEvent.Set();
                }
            }
        }
    }
}
