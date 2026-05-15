using BencodeNET.Torrents;

using Bi2pTorrent.Client.Extensions;
using Bi2pTorrent.Client.Protocol;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Bi2pTorrent.Client;

public class PeerConnection(string myPeerId, Torrent torrent, Peer peer, IPeerEventHandler eventHandler)
{
    private const int MaxBacklog = 5;
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
    private readonly SemaphoreSlim backlogSemaphore = new SemaphoreSlim(MaxBacklog, MaxBacklog);
    private readonly object statsLock = new object();
    private ulong bytesRead = 0;
    private ulong lastBytesRead = 0;
    private ulong bytesSent = 0;
    private ulong lastBytesSent = 0;
    private readonly Dictionary<string, byte> supportedExtensions = [];
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, DateTime> pexAdded = [];
    private readonly ConcurrentDictionary<string, DateTime> pexDropped = [];

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

    public bool IsDead { get; private set; } = false;

    public ConcurrentDictionary<string, DateTime> RemoteAdded { get; private set; } = [];

    public ConcurrentDictionary<string, DateTime> RemoteDropped { get; private set; } = [];

    public async Task<bool> ConnectAsync(TcpClient? tcpClient, Handshake? receiveHandshake = null)
    {
        this.tcpClient = tcpClient;
        this.tcpClient!.ReceiveTimeout = 20_000;
        var stream = this.tcpClient!.GetStream();

        var infoHash = torrent.GetInfoHashBytes();

        var sendHandshake = new Handshake(infoHash, myPeerId, true);
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

        if ((receiveHandshake.Reserved[5] & 0x10) != 0)
        {
            Console.WriteLine($"{peer.Address} supports Extension Protocol.");
            this.SendExtensionProtocolHandshake();
        }

        _ = Task.Factory.StartNew(() => ReceiverAsync(cts.Token), TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(() => SenderAsync(cts.Token), TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(() => DownloadManagerAsync(cts.Token), TaskCreationOptions.LongRunning);

        this.heartbeatTimer.Elapsed += (s, e) =>
        {
            if (this.IsDead)
            {
                return;
            }

            this.messageQueue.Enqueue(new KeepAliveMessage());
            this.messageQueueEvent.Set();
        };
        this.heartbeatTimer.AutoReset = false;
        this.heartbeatTimer.Start();

        this.statsTimer.Elapsed += (s, e) =>
        {
            if (this.IsDead)
            {
                return;
            }

            lock (this.statsLock)
            {
                Console.WriteLine($"{peer.Address}: up: {(bytesSent - lastBytesSent) / 1024.0 / 10.0:N1} kB/s, down: {(bytesRead - lastBytesRead) / 1024.0 / 10.0:N1} kB/s");
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

    public void SendPex(string[] peerAddresses, string[] droppedAddresses)
    {
        if (!this.supportedExtensions.TryGetValue("i2p_pex", out byte extId))
        {
            return;
        }

        foreach (var peerAddress in peerAddresses)
        {
            this.pexDropped.Remove(peerAddress, out _);
        }

        foreach (var dropped in droppedAddresses)
        {
            this.pexAdded.Remove(dropped, out _);
        }

        var i2pPexMessage = new Protocol.ExtensionProtocol.I2pPexMessage
        {
            ExtendedMessageId = extId,
            AddedPeers = peerAddresses.Except(pexAdded.Keys).Except([peer.Address]).ToList(),
            DroppedPeers = droppedAddresses.Except(pexDropped.Keys).ToList(),
        };

        if (i2pPexMessage.AddedPeers.Count == 0 && i2pPexMessage.DroppedPeers.Count == 0)
        {
            return;
        }

        var message = new ExtendedMessage(i2pPexMessage);

        foreach (var added in peerAddresses)
        {
            this.pexAdded[added] = DateTime.UtcNow;
        }

        foreach (var dropped in droppedAddresses)
        {
            this.pexDropped[dropped] = DateTime.UtcNow;
        }

        this.messageQueue.Enqueue(message);
        this.messageQueueEvent.Set();
    }

    public void Disconnect()
    {
        Console.WriteLine($"---> {peer.Address} - Disconnecting");
        Console.Out.Flush();

        this.statsTimer.Stop();
        this.heartbeatTimer.Stop();
        this.cts.Cancel();
        this.tcpClient?.Close();

        this.downloadQueueEvent.Close();
        this.messageQueueEvent.Close();

        this.downloadQueue.Clear();
        this.messageQueue.Clear();
        this.downloadMemory.Clear();
        this.uploadMemory.Clear();
    }

    private void SendExtensionProtocolHandshake()
    {
        var handshake = new Protocol.ExtensionProtocol.HandshakeMessage();
        handshake.Reqq = MaxBacklog;
        handshake.Version = "Bi2pTorrent 0.1";
        handshake.MetadataSize = torrent.GetInfoSize();
        handshake.SupportedExtensions["i2p_pex"] = 1;

        this.messageQueue.Enqueue(new ExtendedMessage(handshake));
        this.messageQueueEvent.Set();
    }

    private async Task ReceiverAsync(CancellationToken ct)
    {
        this.tcpClient!.ReceiveTimeout = 180_000;    // 3 minutes
        var stream = this.tcpClient!.GetStream();
        Memory<byte> buffer = new byte[32 * 1024];
        var lengthBytes = new byte[4];

        while (this.tcpClient.Connected && !ct.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(lengthBytes, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

            if (length == 0)
            {
                // Heartbeat message, ignore
                Console.WriteLine($"{peer.Address} -> Heartbeat");
                continue;
            }

            if (length > buffer.Length)
            {
                Console.WriteLine($"{peer.Address} - Invalid message length {length}, closing connection.");
                this.tcpClient.Close();
                break;
            }

            var bufferSlice = buffer.Slice(0, length);

            try
            {
                await stream.ReadExactlyAsync(bufferSlice, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                break;
            }

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
                    if (this.downloadMemory.TryGetValue(pieceMessage.PieceIndex, out MemoryPiece? value))
                    {
                        value.Write(pieceMessage.GetData(), pieceMessage.Begin);
                        complete = value.IsComplete();
                    }
                }

                if (complete)
                {
                    Console.WriteLine($"{peer.Address} -> Received piece {pieceMessage.PieceIndex}");

                    _ = await eventHandler.ReceivedPieceAsync(this, this.downloadMemory[pieceMessage.PieceIndex]);

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
            else if (message is ExtendedMessage extendedMessage)
            {
                if (extendedMessage.Message is Protocol.ExtensionProtocol.HandshakeMessage extHandshake)
                {
                    Console.WriteLine($"{peer.Address} -> Extended Handshake: {extHandshake.Version}, reqq = {extHandshake.Reqq}, m = {string.Join(' ', extHandshake.SupportedExtensions.Select(kv => $"{kv.Key}={kv.Value}"))}");
                    Console.WriteLine($"Metadata size: theirs: {extHandshake.MetadataSize}, ours: {torrent.GetInfoSize()}");

                    foreach (var ext in extHandshake.SupportedExtensions)
                    {
                        this.supportedExtensions[ext.Key] = ext.Value;
                    }
                }
                else if (extendedMessage.Message is Protocol.ExtensionProtocol.I2pPexMessage i2pPex)
                {
                    Console.WriteLine($"{peer.Address} -> I2P PEX: Added peers: {string.Join(", ", i2pPex.AddedPeers)} - flags: {string.Join(",", i2pPex.AddedPeersFlags)} - Dropped peers: {string.Join(", ", i2pPex.DroppedPeers)}");

                    foreach (var added in i2pPex.AddedPeers)
                    {
                        this.RemoteDropped.Remove(added, out _);
                        this.RemoteAdded[added] = DateTime.UtcNow;
                    }

                    foreach (var dropped in i2pPex.DroppedPeers)
                    {
                        this.RemoteAdded.Remove(dropped, out _);
                        this.RemoteDropped[dropped] = DateTime.UtcNow;
                    }

                    eventHandler.AddDiscoveredPeers(this.RemoteAdded.Keys.Select(a => new Peer(a)).ToArray());
                }
                else
                {
                    Console.WriteLine($"{peer.Address} -> Unknown extended type {extendedMessage.Message}");
                }
            }
            else
            {
                Console.WriteLine($"{peer.Address} -> Unknown message type {message}");
            }
        }

        this.MarkAsDead();
    }

    private async Task SenderAsync(CancellationToken ct)
    {
        this.tcpClient!.SendTimeout = 20_000; // 20 seconds
        var stream = this.tcpClient!.GetStream();
        Memory<byte> sendBuffer = new byte[32 * 1024];

        while (this.tcpClient!.Connected && !ct.IsCancellationRequested)
        {
            if (!this.messageQueueEvent.WaitOne(TimeSpan.FromSeconds(5)))
            {
                continue;
            }

            this.heartbeatTimer.Stop();

            while (this.tcpClient!.Connected && this.messageQueue.TryDequeue(out var message))
            {
                Memory<byte> buffer = null;

                if (message is ChokeMessage or UnchokeMessage or InterestedMessage or NotInterestedMessage)
                {
                    buffer = sendBuffer.Slice(0, 4 + 1);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 1);
                    buffer.Span[4] = message.Type;

                    Console.WriteLine($"{peer.Address} <- {message.GetType().Name}");
                }
                else if (message is BitfieldMessage bitfieldMessage)
                {
                    buffer = sendBuffer.Slice(0, 4 + bitfieldMessage.Bitfield.Length + 1);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, bitfieldMessage.Bitfield.Length + 1);
                    buffer.Span[4] = bitfieldMessage.Type;
                    bitfieldMessage.Bitfield.CopyTo(buffer.Slice(5));

                    Console.WriteLine($"{peer.Address} <- Bitfield");
                }
                else if (message is HaveMessage haveMessage)
                {
                    buffer = sendBuffer.Slice(0, 9);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 5);
                    buffer.Span[4] = haveMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5, 4).Span, haveMessage.PieceIndex);
                    await stream.WriteAsync(buffer);

                    Console.WriteLine($"{peer.Address} <- Have {haveMessage.PieceIndex}");
                }
                else if (message is KeepAliveMessage)
                {
                    buffer = sendBuffer.Slice(0, 4);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 0);

                    Console.WriteLine($"{peer.Address} <- Heartbeat");
                }
                else if (message is PieceMessage pieceMessage && !pieceMessage.Cancelled)
                {
                    buffer = sendBuffer.Slice(0, 4 + 9 + pieceMessage.Length); ;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 9 + pieceMessage.Length);
                    buffer.Span[4] = pieceMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5).Span, pieceMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(9).Span, pieceMessage.Begin);

                    lock (this.uploadMemory)
                    {
                        var data = this.uploadMemory[pieceMessage.PieceIndex].Data.Slice(pieceMessage.Begin, pieceMessage.Length);
                        data.CopyTo(buffer.Slice(13));
                    }
                }
                else if (message is RequestMessage requestMessage && !this.RemoteChoked)
                {
                    buffer = sendBuffer.Slice(0, 17);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Span, 13);
                    buffer.Span[4] = requestMessage.Type;
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5).Span, requestMessage.PieceIndex);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(9).Span, requestMessage.Begin);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(13).Span, requestMessage.Length);
                }
                else if (message is ExtendedMessage extendedMessage)
                {
                    if (extendedMessage.Message is Protocol.ExtensionProtocol.HandshakeMessage extHandshake)
                    {
                        var extHandshakeData = extHandshake.EncodeAsBytes();
                        buffer = sendBuffer.Slice(0, 4 + extHandshakeData.Length + 2);
                        BinaryPrimitives.WriteInt32BigEndian(buffer.Span, extHandshakeData.Length + 2);
                        buffer.Span[4] = extendedMessage.Type;
                        buffer.Span[5] = extHandshake.ExtendedMessageId;
                        extHandshakeData.CopyTo(buffer.Slice(6));

                        Console.WriteLine($"{peer.Address} <- Extended Handshake: {extHandshake.Version}, reqq = {extHandshake.Reqq}, m = {string.Join(' ', extHandshake.SupportedExtensions.Select(kv => $"{kv.Key}={kv.Value}"))}");
                    }
                    else if (extendedMessage.Message is Protocol.ExtensionProtocol.I2pPexMessage i2pPex)
                    {
                        try
                        {
                            var i2pPexData = i2pPex.EncodeAsBytes();
                            buffer = sendBuffer.Slice(0, 4 + i2pPexData.Length + 2);
                            BinaryPrimitives.WriteInt32BigEndian(buffer.Span, i2pPexData.Length + 2);
                            buffer.Span[4] = extendedMessage.Type;
                            buffer.Span[5] = i2pPex.ExtendedMessageId;
                            i2pPexData.CopyTo(buffer.Slice(6));

                            Console.WriteLine($"{peer.Address} <- I2P PEX: {string.Join(", ", i2pPex.AddedPeers)} added, {string.Join(", ", i2pPex.DroppedPeers)} dropped");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{peer.Address} - Failed to encode I2P PEX message: {ex.Message}");
                            continue;
                        }
                    }
                    else
                    {
                        // We don't support sending any other extended messages yet
                        continue;
                    }
                }

                await stream.WriteAsync(buffer, ct);

                lock (this.statsLock)
                {
                    this.bytesSent += (ulong)buffer.Length;
                }
            }

            await stream.FlushAsync(ct);
            this.heartbeatTimer.Start();
        }

        this.MarkAsDead();
    }

    private async Task DownloadManagerAsync(CancellationToken ct)
    {
        while (tcpClient!.Connected && !ct.IsCancellationRequested)
        {
            if (!this.downloadQueueEvent.WaitOne(TimeSpan.FromSeconds(5)))
            {
                continue;
            }

            while (!this.RemoteChoked && this.downloadQueue.TryDequeue(out var pieceIndex))
            {
                Console.WriteLine($"Starting download of piece {pieceIndex}");

                // Last piece is of irregular length
                var pieceSize = torrent.GetPieceSize(pieceIndex);

                lock (this.downloadMemory)
                {
                    this.downloadMemory[pieceIndex] = new MemoryPiece(pieceIndex, new byte[pieceSize]);
                }

                for (int offset = 0; offset < pieceSize; offset += BlockSize)
                {
                    int blockSize = (int)Math.Min(BlockSize, pieceSize - offset);

                    try
                    {
                        await this.backlogSemaphore.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        this.MarkAsDead();
                        return;
                    }

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

        this.MarkAsDead();
    }

    private void MarkAsDead()
    {
        Console.WriteLine($"{peer.Address} - Marked as dead.");
        this.IsDead = true;
    }
}
