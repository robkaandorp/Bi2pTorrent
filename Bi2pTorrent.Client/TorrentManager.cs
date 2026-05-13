using Bi2pTorrent.Client.Extensions;
using Bi2pTorrent.Client.Protocol;

namespace Bi2pTorrent.Client;

public class TorrentManager(TorrentState torrentState, FileManager fileManager) : IPeerEventHandler
{
    private readonly PieceMap pieceMap = new PieceMap(torrentState.Torrent.NumberOfPieces);
    private ConnectionManager? connectionManager;

    public void SetConnectionManager(ConnectionManager connectionManager)
    {
        this.connectionManager = connectionManager;
    }

    public void BitfieldChanged(PeerConnection peerConnection, Bitfield bitfield)
    {
        this.pieceMap.AddPeerBitfield(peerConnection.Peer, bitfield);
        var interesting = bitfield.And(torrentState.Bitfield.Not());

        if (interesting.CompletedPieceCount > 0)
        {
            if (!peerConnection.LocalInterested)
            {
                peerConnection.SetInterested(true);

                if (!peerConnection.RemoteChoked)
                {
                    this.AssignPiecesToPeer(peerConnection);
                }
            }
        }
        else
        {
            if (peerConnection.LocalInterested)
            {
                peerConnection.SetInterested(false);
            }
        }
    }

    public void RemoteChokedChanged(PeerConnection peerConnection, bool choked)
    {
        if (!choked)
        {
            this.AssignPiecesToPeer(peerConnection);
        }
    }

    public void RemoteInterestedChanged(PeerConnection peerConnection, bool interested)
    {
        if (interested)
        {
            peerConnection.SetChoked(false);
        }
    }

    public async Task<bool> ReceivedPieceAsync(PeerConnection peerConnection, MemoryPiece memoryPiece)
    {
        if (!torrentState.CheckPiece(memoryPiece))
        {
            Console.WriteLine($"Received invalid piece {memoryPiece.PieceIndex} from {peerConnection.Peer.Address}");
            return false;
        }

        await fileManager.WritePieceAsync(torrentState.Torrent, memoryPiece.PieceIndex, memoryPiece.Data);
        torrentState.Bitfield.SetPiece(memoryPiece.PieceIndex);
        connectionManager!.HavePiece(memoryPiece.PieceIndex);

        if (torrentState.Bitfield.IsComplete)
        {
            connectionManager.SetInterested(false);
        }
        else
        {
            this.AssignPiecesToPeer(peerConnection);
        }

        return true;
    }

    public async Task<MemoryPiece> LoadPieceAsync(PeerConnection peerConnection, int pieceIndex)
    {
        Memory<byte> pieceData = new byte[torrentState.Torrent.GetPieceSize(pieceIndex)];
        await fileManager.ReadPieceAsync(torrentState.Torrent, pieceIndex, pieceData);
        
        return new MemoryPiece(pieceIndex, torrentState.Torrent.GetPieceSize(pieceIndex), pieceData);
    }

    private void AssignPiecesToPeer(PeerConnection peerConnection)
    {
        if (peerConnection.BusyPieces.Length >= 2)
        {
            return;
        }

        var interesting = peerConnection.Bitfield.And(torrentState.Bitfield.Not());
        var interestingPieces = interesting.GetPieceIndices()
            .Except(this.connectionManager!.Peers.SelectMany(p => p.BusyPieces))
            .ToArray();

        var orderedInterestingPieces = interestingPieces.Select(i => (index: i, count: pieceMap.GetCount(i)))
            .OrderBy(i => i.count)
            .ThenBy(i => Random.Shared.NextDouble())
            .Select(i => i.index)
            .ToArray();

        var piecesToDownload = orderedInterestingPieces.Take(2 - peerConnection.BusyPieces.Length);

        if (!piecesToDownload.Any())
        {
            return;
        }

        foreach (var piece in piecesToDownload)
        {
            peerConnection.AssignDownloadPiece(piece);
        }

        Console.WriteLine($"Added to download queue: {string.Join(", ", piecesToDownload)}");
    }
}
