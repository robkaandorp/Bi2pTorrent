using Bi2pTorrent.Client.Protocol;

namespace Bi2pTorrent.Client;

public interface IPeerEventHandler
{
    void RemoteChokedChanged(PeerConnection peerConnection, bool choked);

    void RemoteInterestedChanged(PeerConnection peerConnection, bool interested);

    void BitfieldChanged(PeerConnection peerConnection, Bitfield bitfield);

    Task<bool> ReceivedPieceAsync(PeerConnection peerConnection, MemoryPiece memoryPiece);

    Task<MemoryPiece> LoadPieceAsync(PeerConnection peerConnection, int pieceIndex);

    void PeersDiscovered(PeerConnection peerConnection, Peer[] peers);
}
