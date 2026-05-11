using BencodeNET.Torrents;

using Bi2pTorrent.Client.Extensions;

using System.Security.Cryptography;

namespace Bi2pTorrent.Client;

public class FileManager(string directory)
{
    public void Initialize()
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task EnsureFilesAsync(Torrent torrent)
    {
        var piece = new byte[torrent.PieceSize];

        if (torrent.FileMode == TorrentFileMode.Single)
        {
            string filePath = Path.Combine(directory, torrent.File.FileName);

            if (!File.Exists(filePath))
            {
                await this.CreateEmptyFileAsync(filePath, torrent.File.FileSize, piece);
            }
        }
        else
        {
            var torrentFolder = Path.Combine(directory, torrent.DisplayName);

            foreach (var file in torrent.Files)
            {
                string filePath = Path.Combine(torrentFolder, file.FullPath);

                if (!File.Exists(filePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await this.CreateEmptyFileAsync(filePath, file.FileSize, piece);
                }
            }
        }
    }

    public async Task ScanPiecesAsync(TorrentState torrentState)
    {
        byte[] pieceData = new byte[torrentState.Torrent.PieceSize];

        for (var pieceIndex = 0; pieceIndex < torrentState.Torrent.NumberOfPieces; pieceIndex++)
        {
            var pieceMemory = new Memory<byte>(pieceData, 0, (int)torrentState.Torrent.GetPieceSize(pieceIndex));

            await this.ReadPieceAsync(torrentState.Torrent, pieceIndex, pieceMemory);
            byte[] pieceHash = SHA1.HashData(pieceMemory.Span);

            if (pieceHash.SequenceEqual(torrentState.Torrent.Pieces.AsSpan(pieceIndex * 20, 20)))
            {
                torrentState.SetPiece(pieceIndex);
            }
        }
    }

    public async Task ReadPieceAsync(Torrent torrent, int pieceIndex, Memory<byte> buffer)
    {
        var startByte = (long)pieceIndex * torrent.PieceSize;

        if (torrent.FileMode == TorrentFileMode.Single)
        {
            string filePath = Path.Combine(directory, torrent.File.FileName);
            using var fileStream = File.OpenRead(filePath);
            fileStream.Seek(startByte, SeekOrigin.Begin);
            await fileStream.ReadExactlyAsync(buffer);
        }
        else
        {
            long bytesToSkip = startByte;
            int pieceOffset = 0;

            foreach (var file in torrent.Files)
            {
                if (bytesToSkip >= file.FileSize)
                {
                    bytesToSkip -= file.FileSize;
                    continue;
                }

                string filePath = Path.Combine(directory, torrent.DisplayName, file.FullPath);

                using var fileStream = File.OpenRead(filePath);
                fileStream.Seek(bytesToSkip, SeekOrigin.Begin);
                
                int bytesToRead = (int)Math.Min(buffer.Length - pieceOffset, file.FileSize - bytesToSkip);
                await fileStream.ReadExactlyAsync(buffer.Slice(pieceOffset, bytesToRead));
                
                pieceOffset += bytesToRead;
                bytesToSkip = 0;

                if (pieceOffset >= buffer.Length)
                {
                    break;
                }
            }
        }
    }

    public async Task WritePieceAsync(Torrent torrent, int pieceIndex, byte[] piece)
    {
        // TODO: Implement writing a piece to the correct file(s) based on the torrent's file structure and piece size.
        var startByte = (long)pieceIndex * torrent.PieceSize;

        if (torrent.FileMode == TorrentFileMode.Single)
        {
            string filePath = Path.Combine(directory, torrent.File.FileName);
            using var fileStream = File.OpenWrite(filePath);
            fileStream.Seek(startByte, SeekOrigin.Begin);
            await fileStream.WriteAsync(piece);
        }
        else
        {
            long bytesToSkip = startByte;
            int pieceOffset = 0;

            foreach (var file in torrent.Files)
            {
                if (bytesToSkip >= file.FileSize)
                {
                    bytesToSkip -= file.FileSize;
                    continue;
                }

                string filePath = Path.Combine(directory, torrent.DisplayName, file.FullPath);
                using var fileStream = File.OpenWrite(filePath);
                fileStream.Seek(bytesToSkip, SeekOrigin.Begin);

                int bytesToWrite = (int)Math.Min(piece.Length - pieceOffset, file.FileSize - bytesToSkip);
                await fileStream.WriteAsync(piece.AsMemory(pieceOffset, bytesToWrite));

                pieceOffset += bytesToWrite;
                bytesToSkip = 0;

                if (pieceOffset >= piece.Length)
                {
                    break;
                }
            }
        }
    }

    private async Task CreateEmptyFileAsync(string filePath, long fileSize, byte[] piece)
    {
        using var fileStream = File.OpenWrite(filePath);
        long pos = piece.Length;

        for (; pos < fileSize; pos += piece.Length)
        {
            await fileStream.WriteAsync(piece, 0, piece.Length);
        }

        await fileStream.WriteAsync(piece, 0, (int)(fileSize % piece.Length));
        await fileStream.FlushAsync();
    }
}
