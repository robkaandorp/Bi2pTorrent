using System.Numerics;

namespace Bi2pTorrent.Client.Protocol;

public class Bitfield
{
    private int pieceCount;
    private byte[] bitfield;

    public byte[] Bytes => bitfield.ToArray();

    public Bitfield(int pieceCount, byte[] bitfield)
    {
        this.pieceCount = pieceCount;
        this.bitfield = bitfield;
    }

    public Bitfield(int pieceCount)
    {
        this.pieceCount = pieceCount;
        this.bitfield = new byte[(pieceCount + 7) / 8];
    }

    public bool HasPiece(int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        int bitIndex = pieceIndex % 8;

        if (byteIndex >= bitfield.Length)
        {
            return false;
        }

        return (bitfield[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }

    public void SetPiece(int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        int bitIndex = pieceIndex % 8;

        if (byteIndex >= bitfield.Length)
        {
            throw new IndexOutOfRangeException("Piece index is out of range of the bitfield.");
        }

        bitfield[byteIndex] |= (byte)(1 << (7 - bitIndex));
    }

    public void UnsetPiece(int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        int bitIndex = pieceIndex % 8;

        if (byteIndex >= bitfield.Length)
        {
            throw new IndexOutOfRangeException("Piece index is out of range of the bitfield.");
        }

        bitfield[byteIndex] &= (byte)~(1 << (7 - bitIndex));
    }

    public long CompletedPieceCount
    {
        get
        {
            long count = 0;

            foreach (var b in bitfield)
            {
                count += this.CountBits(b);
            }

            return count;
        }
    }

    private long CountBits(byte b) => BitOperations.PopCount(b);

    public Bitfield Not()
    {
        // Invert the bits in the bitfield
        var not = new Bitfield(this.pieceCount, this.bitfield.Select(b => (byte)~b).ToArray());

        // Clear any bits that are beyond the piece count
        int excessBits = (this.bitfield.Length * 8) - this.pieceCount;

        if (excessBits > 0)
        {
            for (var i = this.pieceCount; i < this.bitfield.Length * 8; i++)
            {
                not.UnsetPiece(i);
            }
        }

        return not;
    }

    public Bitfield And(Bitfield bitfield)
    {
        if (bitfield.pieceCount != this.pieceCount)
        {
            throw new ArgumentException("Bitfields must be of the same length to perform AND operation.");
        }

        var result = new byte[this.bitfield.Length];

        for (int i = 0; i < this.bitfield.Length; i++)
        {
            result[i] = (byte)(this.bitfield[i] & bitfield.bitfield[i]);
        }

        return new Bitfield(this.pieceCount, result);
    }

    public int[] GetPieceIndices()
    {
        var indices = new List<int>();

        for (int i = 0; i < this.pieceCount; i++)
        {
            if (this.HasPiece(i))
            {
                indices.Add(i);
            }
        }

        return indices.ToArray();
    }

    public bool IsComplete => CompletedPieceCount == this.pieceCount;
}
