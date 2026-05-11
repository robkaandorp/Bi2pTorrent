using Bi2pTorrent.Client.Protocol;

namespace Bi2pTorrent.Client;

public class PieceMap(int pieceCount)
{
    private readonly Dictionary<string, byte[]> pieceMap = new();

    public void AddPeerBitfield(Peer peer, Bitfield bitfield)
    {
        this.pieceMap[peer.Address] = new byte[pieceCount];

        foreach (var pieceIndex in bitfield.GetPieceIndices())
        {
            this.pieceMap[peer.Address][pieceIndex] = 1;
        }
    }

    public void RemovePeer(Peer peer)
    {
        this.pieceMap.Remove(peer.Address);
    }

    public void PeerHas(Peer peer, int pieceIndex)
    {
        if (this.pieceMap.TryGetValue(peer.Address, out var pieces))
        {
            pieces[pieceIndex] = 1;
        }
    }

    public int GetCount(int index)
    {
        return this.pieceMap.Values.Sum(bytes => (int)bytes[index]);
    }
}