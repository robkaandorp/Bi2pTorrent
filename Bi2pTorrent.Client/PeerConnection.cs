using BencodeNET.Torrents;

using Bi2pTorrent.Client.Extensions;
using Bi2pTorrent.Client.Protocol;

using DotI2p;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

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

    public ConcurrentQueue<int> DownloadQueue => this.downloadQueue;

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
        
        var sendHandshake = new Handshake();
        stream.WriteByte(sendHandshake.Length);
        stream.Write(sendHandshake.Protocol);
        stream.Write(sendHandshake.Reserved);
        stream.Write(infoHash);
        stream.Write(Encoding.ASCII.GetBytes(myPeerId));
        await stream.FlushAsync();

        if (receiveHandshake == null)
        {
            try
            {
                receiveHandshake = Handshake.FromStream(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{peer.Address} - Handshake failed: {ex.Message}");
                return false;
            }
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
        var reader = new BinaryReader(this.tcpClient!.GetStream());

        while (this.tcpClient.Connected)
        {
            var length = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());

            if (length == 0)
            {
                // Heartbeat message, ignore
                Console.WriteLine($"{peer.Address} -> Heartbeat");
                continue;
            }

            var type = reader.ReadByte();

            switch (type)
            {
                case 0: // Choke
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
                    break;
                case 1: // Unchoke
                    Console.WriteLine($"{peer.Address} -> Unchoke");

                    if (this.RemoteChoked)
                    {
                        this.RemoteChoked = false;
                        eventHandler.RemoteChokedChanged(this, false);
                    }
                    break;
                case 2: // Interested
                    Console.WriteLine($"{peer.Address} -> Interested");

                    if (!this.RemoteInterested)
                    {
                        this.RemoteInterested = true;
                        eventHandler.RemoteInterestedChanged(this, true);
                    }
                    break;
                case 3: // Not Interested
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
                    break;
                case 4: // Have
                    int pieceIndex = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    Console.WriteLine($"{peer.Address} -> Have {pieceIndex}");
                    this.bitfield.SetPiece(pieceIndex);
                    eventHandler.BitfieldChanged(this, this.bitfield);

                    lock (this.uploadMemory)
                    {
                        if (this.uploadMemory.ContainsKey(pieceIndex))
                        {
                            this.uploadMemory.Remove(pieceIndex);
                        }
                    }
                    break;
                case 5: // Bitfield
                    var bitfield = reader.ReadBytes((int)length - 1);
                    this.bitfield = new Bitfield(torrent.NumberOfPieces, bitfield);
                    Console.WriteLine($"{peer.Address} -> Bitfield ({bitfield.Length} bytes), has {this.bitfield.CompletedPieceCount} of {torrent.NumberOfPieces} pieces = {this.bitfield.CompletedPieceCount * 100.0 / torrent.NumberOfPieces:N1}%");
                    eventHandler.BitfieldChanged(this, this.bitfield);
                    break;
                case 6: // Request
                    var pieceMessage = new PieceMessage(
                        BinaryPrimitives.ReverseEndianness(reader.ReadInt32()),
                        BinaryPrimitives.ReverseEndianness(reader.ReadInt32()),
                        BinaryPrimitives.ReverseEndianness(reader.ReadInt32()));
                    Console.WriteLine($"{peer.Address} -> Request piece {pieceMessage.PieceIndex}, begin {pieceMessage.Begin}, length {pieceMessage.Length}");

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
                    break;
                case 7: // Piece
                    this.backlogSemaphore.Release();
                    int pieceIndex2 = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    int begin2 = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    var block = reader.ReadBytes((int)length - 9);
                    var complete = false;

                    lock (this.downloadMemory)
                    {
                        if (this.downloadMemory.ContainsKey(pieceIndex2))
                        {
                            this.downloadMemory[pieceIndex2].Write(block, begin2, block.Length);
                            complete = this.downloadMemory[pieceIndex2].IsComplete();
                        }
                    }

                    if (complete)
                    {
                        Console.WriteLine($"{peer.Address} -> Received piece {pieceIndex2}");
                        await eventHandler.ReceivedPieceAsync(this, this.downloadMemory[pieceIndex2]);

                        lock (this.downloadMemory)
                        {
                            this.downloadMemory.Remove(pieceIndex2);
                        }
                    }
                    break;
                case 8: // Cancel
                    int cancelPieceIndex = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    int cancelBegin = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    int cancelLength = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
                    Console.WriteLine($"{peer.Address} -> Cancel piece {cancelPieceIndex}, begin {cancelBegin}, length {cancelLength}");

                    foreach (var item in this.messageQueue)
                    {
                        if (item is PieceMessage pieceMsg &&
                            pieceMsg.PieceIndex == cancelPieceIndex && pieceMsg.Begin == cancelBegin && pieceMsg.Length == cancelLength)
                        {
                            pieceMsg.Cancelled = true;
                        }
                    }
                    break;
                default:
                    Console.WriteLine($"{peer.Address} -> Unknown message type {type}");
                    break;
            }

            lock (statsLock)
            {
                bytesRead += 4 + (ulong)length;
            }
        }
    }

    private async Task SenderAsync()
    {
        var stream = this.tcpClient!.GetStream();

        while (this.tcpClient!.Connected)
        {
            this.messageQueueEvent.WaitOne();

            while (this.tcpClient!.Connected && this.messageQueue.TryDequeue(out var message))
            {
                int bytesWritten = 0;

                if (message is ChokeMessage or UnchokeMessage or InterestedMessage or NotInterestedMessage)
                {
                    var buffer = new byte[5];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, 1);
                    buffer[4] = message.Type;
                    await stream.WriteAsync(buffer);
                    Console.WriteLine($"{peer.Address} <- {message.GetType().Name}");
                    bytesWritten += buffer.Length;
                }
                else if (message is BitfieldMessage bitfieldMessage)
                {
                    var buffer = new byte[5];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, bitfieldMessage.Bitfield.Length + 1);
                    buffer[4] = bitfieldMessage.Type;
                    await stream.WriteAsync(buffer);
                    bytesWritten += buffer.Length;
                    await stream.WriteAsync(bitfieldMessage.Bitfield);
                    bytesWritten += bitfieldMessage.Bitfield.Length;
                    Console.WriteLine($"{peer.Address} <- Bitfield");
                }
                else if (message is HaveMessage haveMessage)
                {
                    var buffer = new byte[9];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, 5);
                    buffer[4] = haveMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(5), haveMessage.PieceIndex);
                    await stream.WriteAsync(buffer);
                    bytesWritten += buffer.Length;
                    Console.WriteLine($"{peer.Address} <- Have {haveMessage.PieceIndex}");
                }
                else if (message is KeepAliveMessage)
                {
                    var buffer = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, 0);
                    await stream.WriteAsync(buffer);
                    bytesWritten += buffer.Length;
                    Console.WriteLine($"{peer.Address} <- Heartbeat");
                }
                else if (message is PieceMessage pieceMessage && !pieceMessage.Cancelled)
                {
                    var buffer = new byte[13];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, 13 + pieceMessage.Length);
                    buffer[4] = pieceMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(5), pieceMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(9), pieceMessage.Begin);
                    await stream.WriteAsync(buffer);

                    byte[] data;

                    lock (this.uploadMemory)
                    {
                        data = this.uploadMemory[pieceMessage.PieceIndex].Data;
                    }

                    await stream.WriteAsync(data);
                }
                else if (message is RequestMessage requestMessage && !this.RemoteChoked)
                {
                    var buffer = new byte[17];
                    BinaryPrimitives.WriteInt32BigEndian(buffer, 13);
                    buffer[4] = requestMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(5), requestMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(9), requestMessage.Begin);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(13), requestMessage.Length);
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
                    this.downloadMemory[pieceIndex] = new MemoryPiece(pieceIndex, pieceSize);
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
