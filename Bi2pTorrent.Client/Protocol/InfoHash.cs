using System.Text;

namespace Bi2pTorrent.Client.Protocol;

public class InfoHash
{
    private readonly ReadOnlyMemory<byte> infoHash;

    public InfoHash(byte[] infoHash)
    {
        this.infoHash = infoHash;
    }

    public ReadOnlyMemory<byte> GetBytes() => this.infoHash;

    public string GetHexString() => BitConverter.ToString(this.infoHash.ToArray()).Replace("-", "").ToLowerInvariant();

    public string GetUriString() => string.Join("", this.infoHash.ToArray().Select(b => $"%{b:X2}"));
}
