using System.Text;

namespace Bi2pTorrent.Client.Protocol;

public class Handshake
{
    public static async Task<Handshake> FromStreamAsync(Stream stream)
    {
        var handshake = new Handshake();
        await stream.ReadExactlyAsync(handshake.bytes);

        return handshake;
    }

    private byte[] bytes = new byte[68];

    public byte Length
    {
        get => this.bytes[0];
        set => this.bytes[0] = value;
    }

    public string Protocol
    {
        get => Encoding.ASCII.GetString(this.bytes.AsSpan(1, 19));
        set => Encoding.ASCII.GetBytes(value).CopyTo(this.bytes.AsSpan(1, 19));
    }

    public byte[] Reserved
    {
        get => this.bytes.AsSpan(20, 8).ToArray();
        set => value.CopyTo(this.bytes.AsSpan(20, 8));
    }

    public byte[] InfoHash
    {
        get => this.bytes.AsSpan(28, 20).ToArray();
        set => value.CopyTo(this.bytes.AsSpan(28, 20));
    }

    public string PeerId
    {
        get => Encoding.ASCII.GetString(this.bytes.AsSpan(48, 20));
        set => Encoding.ASCII.GetBytes(value).CopyTo(this.bytes.AsSpan(48, 20));
    }

    public Handshake() { }

    public Handshake(byte[] infoHash, string peerId)
    {
        this.Length = 19;
        this.Protocol = "BitTorrent protocol";
        this.InfoHash = infoHash;
        this.PeerId = peerId;
    }

    public async Task ToStreamAsync(Stream stream)
    {
        await stream.WriteAsync(this.bytes);
    }
}
