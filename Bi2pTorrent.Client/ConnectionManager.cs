using BencodeNET.Torrents;

using DotI2p;

using System.Net.Sockets;

namespace Bi2pTorrent.Client;

public class ConnectionManager(SamSession protocolSession, string myPeerId, Torrent torrent, TorrentManager torrentManager, TorrentState torrentState)
{
    private readonly Dictionary<string, PeerConnection> peers = [];

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

    public async Task StartAsync()
    {
        _ = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                var virtualStream = protocolSession.CreateVirtualStream();
                var acceptedConnection = await virtualStream.AcceptAsync();

                var peer = new Peer(acceptedConnection.Destination.GetB32Hostname());
                var peerConnection = new PeerConnection(protocolSession, myPeerId, torrent, peer, torrentManager);

                try
                {
                    await this.ConnectPeerAsync(peer, peerConnection, acceptedConnection.TcpClient);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{peer.Address} - Connection failed: {ex.Message}");
                    virtualStream.Dispose();
                }
            }
        }, TaskCreationOptions.LongRunning);
    }

    public async Task AddPeerAsync(Peer peer)
    {
        if (peer.Address == protocolSession.Destination?.GetB32Hostname())
        {
            return;
        }

        lock (this.peers)
        {
            if (this.peers.ContainsKey(peer.Address))
            {
                return;
            }
        }

        var peerConnection = new PeerConnection(protocolSession, myPeerId, torrent, peer, torrentManager);
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
        }
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

    private async Task ConnectPeerAsync(Peer peer, PeerConnection peerConnection, TcpClient tcpClient)
    {
        if (await peerConnection.ConnectAsync(tcpClient))
        {
            lock (peers)
            {
                peers.Add(peer.Address, peerConnection);
            }

            peerConnection.SendBitfield(torrentState.Bitfield);
        }
    }
}
