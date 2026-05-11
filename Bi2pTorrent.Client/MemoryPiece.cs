namespace Bi2pTorrent.Client;

public class MemoryPiece(int pieceIndex, long pieceSize)
{
    private readonly byte[] data = new byte[pieceSize];
    private readonly List<(int begin, int length)> written = [];

    public int PieceIndex => pieceIndex;

    public byte[] Data => data;

    public void Write(byte[] buffer, int begin, int length)
    {
        if (begin < 0 || length < 0 || begin + length > this.data.Length)
        {
            throw new ArgumentOutOfRangeException("Begin and length must specify a valid range within the piece.");
        }

        if (this.written.Any(i => i.begin == begin))
        {
            throw new InvalidOperationException("This range has already been written.");
        }

        Array.Copy(buffer, 0, this.data, begin, length);
        this.written.Add((begin, length));
    }

    public bool IsComplete()
    {
        long totalWritten = 0;

        foreach (var (begin, length) in this.written)
        {
            totalWritten += length;
        }

        return totalWritten >= this.data.Length;
    }
}
