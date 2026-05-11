using System.Text;

namespace Bi2pTorrent.Client.Protocol;

public class Handshake
{
    public static Handshake FromStream(Stream stream)
    {
        var handshake = new Handshake();
        handshake.Length = (byte)stream.ReadByte();
        stream.ReadExactly(handshake.Protocol, 0, handshake.Length);
        stream.ReadExactly(handshake.Reserved, 0, 8);
        stream.ReadExactly(handshake.InfoHash, 0, 20);
        stream.ReadExactly(handshake.PeerId, 0, 20);

        return handshake;
    }

    public byte Length { get; set; } = 19;

    public byte[] Protocol { get; set; } = Encoding.ASCII.GetBytes("BitTorrent protocol");
    
    public byte[] Reserved { get; set; } = new byte[8];
    
    public byte[] InfoHash { get; set; } = new byte[20];
    
    public byte[] PeerId { get; set; } = new byte[20];
}
