namespace Bi2pTorrent.Client.Protocol;

public interface IMessage
{
    byte Type { get; }
}

public class ChokeMessage : IMessage
{
    public byte Type => 0;
}

public class UnchokeMessage : IMessage
{
    public byte Type => 1;
}

public class InterestedMessage : IMessage
{
    public byte Type => 2;
}

public class NotInterestedMessage : IMessage
{
    public byte Type => 3;
}

public class HaveMessage : IMessage
{
    public byte Type => 4;

    public int PieceIndex { get; set;  }
}

public record BitfieldMessage(byte[] Bitfield) : IMessage
{
    public byte Type => 5;
}

public record RequestMessage(int PieceIndex, int Begin, int Length) : IMessage
{
    public byte Type => 6;
}

public record PieceMessage(int PieceIndex, int Begin, int Length) : IMessage
{
    private ReadOnlyMemory<byte> data;

    public byte Type => 7;

    public bool Cancelled { get; set; }

    public void SetData(ReadOnlyMemory<byte> data)
    {
        if (data.Length != Length)
        {
            throw new ArgumentException($"Data length must be equal to the specified length of {Length} bytes.");
        }

        this.data = data;
    }

    public ReadOnlyMemory<byte> GetData() => this.data;
}

public record CancelMessage(int PieceIndex, int Begin, int Length) : IMessage
{
    public byte Type => 8;
}

public class KeepAliveMessage : IMessage
{
    public byte Type => 255; // Special value to indicate keep-alive
}

public record UnknownMessage(byte Type, int Length) : IMessage;