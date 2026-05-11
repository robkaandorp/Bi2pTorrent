using BencodeNET.Torrents;

namespace Bi2pTorrent.Client.Extensions;

public static class TorrentExtensions
{
    public static long GetPieceSize(this Torrent torrent, int pieceIndex)
    {
        var pieceSize = torrent.PieceSize;

        if (pieceIndex == torrent.NumberOfPieces - 1)
        {
            pieceSize = torrent.TotalSize - (long)pieceIndex * torrent.PieceSize;
        }

        return pieceSize;
    }
}
