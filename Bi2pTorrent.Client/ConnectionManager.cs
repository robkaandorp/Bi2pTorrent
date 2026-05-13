using BencodeNET.Torrents;

using Bi2pTorrent.Client.Protocol;

using DotI2p;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Bi2pTorrent.Client;

public class ConnectionManager(SamSession protocolSession, string myPeerId, Torrent torrent, TorrentManager torrentManager, TorrentState torrentState)
{
    private readonly string myAddress = protocolSession.Destination!.GetB32Hostname();
    private readonly ConcurrentDictionary<string, bool> discoveredPeers = [];   // Bool is true when peer is in connecting state.
    private readonly ConcurrentDictionary<string, PeerConnection> peers = [];
    private readonly ConcurrentDictionary<string, DateTime> dropped = [];
    private readonly SemaphoreSlim concurrentConnects = new SemaphoreSlim(5);

    public PeerConnection[] Peers
    {
        get
        {
            lock (this.peers)
            {
                return this.peers.Values.ToArray();
            }
        }
    }

    public Torrent Torrent => torrent;

    public async Task StartAsync()
    {
        _ = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                // Find dead peers and move them to the dropped list
                foreach (var kvp in this.peers)
                {
                    var peerAddress = kvp.Key;
                    var peerConnection = kvp.Value;

                    if (peerConnection.IsDead)
                    {
                        this.dropped[peerAddress] = DateTime.UtcNow;
                        this.peers.TryRemove(peerAddress, out _);
                        peerConnection.Disconnect();
                        Console.WriteLine($"{peerAddress} - Peer disconnected");
                    }
                }

                foreach (var peer in this.peers.Values)
                {
                    this.AddDiscoveredPeers(peer.RemoteAdded.Keys.ToArray());
                }

                // Connect to discovered peers
                var peerCount = this.peers.Count + this.discoveredPeers.Count(kvp => kvp.Value);
                var peersToAdd = discoveredPeers
                    .Where(kvp => !kvp.Value)   // Only consider peers that are not already in connecting state
                    .Select(kvp => kvp.Key)
                    .OrderBy(_ => Random.Shared.NextDouble())
                    .Take(50 - peerCount)
                    .ToArray();

                foreach (var peerToAdd in peersToAdd)
                {
                    this.concurrentConnects.Wait();
                    this.discoveredPeers[peerToAdd] = true; // Mark as connecting
                    _ = this.AddPeerAsync(new Peer(peerToAdd));
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }, TaskCreationOptions.LongRunning);
    }

    private async Task AddPeerAsync(Peer peer)
    {
        if (peer.Address == myAddress)
        {
            this.discoveredPeers.TryRemove(peer.Address, out _);
            this.concurrentConnects.Release();
            return;
        }

        lock (this.peers)
        {
            if (this.peers.ContainsKey(peer.Address))
            {
                this.discoveredPeers.TryRemove(peer.Address, out _);
                this.concurrentConnects.Release();
                return;
            }
        }

        var peerConnection = new PeerConnection(myPeerId, torrent, peer, torrentManager);
        var virtualStream = protocolSession.CreateVirtualStream();

        try
        {
            var tcpClient = await virtualStream.ConnectAsync(new DestinationKey(peer.Address));
            await this.ConnectPeerAsync(peer, peerConnection, tcpClient);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{peer.Address} - Connection failed: {ex.Message}");
            virtualStream.Dispose();
            this.dropped[peer.Address] = DateTime.UtcNow;
            this.discoveredPeers.TryRemove(peer.Address, out _);
        }

        this.concurrentConnects.Release();
    }

    public async Task<bool> TryAddPeerFromListener(Peer peer, AcceptedConnection acceptedConnection, Handshake handshake)
    {
        if (peer.Address == myAddress ||
            this.discoveredPeers.TryGetValue(peer.Address, out bool connecting) && connecting ||
            this.peers.ContainsKey(peer.Address))
        {
            acceptedConnection.TcpClient.Dispose();
            return false;
        }

        var peerConnection = new PeerConnection(myPeerId, torrent, peer, torrentManager);
        await this.ConnectPeerAsync(peer, peerConnection, acceptedConnection.TcpClient, handshake);

        return true;
    }

    public void HavePiece(int pieceIndex)
    {
        lock (this.peers)
        {
            foreach (var peerConnection in this.peers.Values)
            {
                peerConnection.SendHave(pieceIndex);
            }
        }
    }

    public void SetInterested(bool interested)
    {
        lock (this.peers)
        {
            foreach (var peerConnection in this.peers.Values)
            {
                peerConnection.SetInterested(interested);
            }
        }
    }

    public void AddDiscoveredPeers(string[] addresses)
    {
        foreach (string address in addresses)
        {
            if (address == myAddress)
            {
                continue;
            }

            if (this.peers.ContainsKey(address))
            {
                continue;
            }

            if (this.dropped.TryGetValue(address, out DateTime value))
            {
                if (value > DateTime.UtcNow.AddMinutes(-5))
                {
                    continue;
                }
                
                this.dropped.TryRemove(address, out _);
            }

            this.discoveredPeers.TryAdd(address, false);
        }
    }

    private async Task ConnectPeerAsync(Peer peer, PeerConnection peerConnection, TcpClient tcpClient, Handshake? handshake = null)
    {
        if (await peerConnection.ConnectAsync(tcpClient, handshake))
        {
            lock (peers)
            {
                if (!peers.TryAdd(peer.Address, peerConnection))
                {
                    throw new InvalidOperationException($"Failed to add peer: {peer.Address}");
                }
            }

            this.discoveredPeers.TryRemove(peer.Address, out _);
            this.dropped.TryRemove(peer.Address, out _);

            peerConnection.SendBitfield(torrentState.Bitfield);
        }
    }
}
