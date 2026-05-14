using BencodeNET.Torrents;

using Bi2pTorrent.Client.Extensions;
using Bi2pTorrent.Client.Protocol;

using System.Security.Cryptography;

namespace Bi2pTorrent.Client;

public class TorrentState(Torrent torrent)
{
    private Bitfield bitfield = new Bitfield(torrent.NumberOfPieces);

    public void SetPiece(int pieceIndex)
    {
        this.bitfield.SetPiece(pieceIndex);
    }

    public Torrent Torrent { get => torrent; }

    public Bitfield Bitfield { get => this.bitfield; }

    public bool CheckPiece(MemoryPiece memoryPiece)
    {
        byte[] pieceHash = SHA1.HashData(memoryPiece.Data.Span);

        return pieceHash.SequenceEqual(torrent.Pieces.AsSpan(memoryPiece.PieceIndex * 20, 20));
    }

    public long BytesCompleted()
    {
        if (this.bitfield.HasPiece(this.Torrent.NumberOfPieces - 1))
        {
            return (this.bitfield.CompletedPieceCount - 1) * this.Torrent.PieceSize + this.Torrent.GetPieceSize(this.Torrent.NumberOfPieces - 1);
        }

        return this.bitfield.CompletedPieceCount * this.Torrent.PieceSize;
    }
}
