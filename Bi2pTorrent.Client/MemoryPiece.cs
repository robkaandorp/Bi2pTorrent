namespace Bi2pTorrent.Client;

public class MemoryPiece(int pieceIndex, Memory<byte> data)
{
    private readonly Memory<byte> data = data;
    private readonly List<(int begin, int length)> written = [];

    public int PieceIndex => pieceIndex;

    public ReadOnlyMemory<byte> Data => data;

    public void Write(ReadOnlyMemory<byte> buffer, int begin)
    {
        if (begin < 0 || begin + buffer.Length > this.data.Length)
        {
            throw new ArgumentOutOfRangeException("Begin and length must specify a valid range within the piece.");
        }

        if (this.written.Any(i => i.begin == begin))
        {
            throw new InvalidOperationException("This range has already been written.");
        }

        buffer.CopyTo(this.data.Slice(begin));
        this.written.Add((begin, buffer.Length));
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
