using BencodeNET.Torrents;

using Bi2pTorrent.Client.Protocol;

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

    public Torrent Torrent => torrent;

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
        }
    }

    public async Task AddPeerFromListener(Peer peer, AcceptedConnection acceptedConnection, Handshake handshake)
    {
        var peerConnection = new PeerConnection(myPeerId, torrent, peer, torrentManager);
        await this.ConnectPeerAsync(peer, peerConnection, acceptedConnection.TcpClient, handshake);
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

    private async Task ConnectPeerAsync(Peer peer, PeerConnection peerConnection, TcpClient tcpClient, Handshake? handshake = null)
    {
        if (await peerConnection.ConnectAsync(tcpClient, handshake))
        {
            lock (peers)
            {
                peers.Add(peer.Address, peerConnection);
            }

            peerConnection.SendBitfield(torrentState.Bitfield);
        }
    }
}
