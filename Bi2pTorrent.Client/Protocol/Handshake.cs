using System;
using System.Collections.Generic;
using System.Text;

namespace Bi2pTorrent.Client.Protocol;

public struct Handshake
{
    public byte Length = 19;
    public byte[] Protocol = Encoding.ASCII.GetBytes("BitTorrent protocol");
    public byte[] Reserved = new byte[8];
    public byte[] InfoHash = new byte[20];
    public byte[] PeerId = new byte[20];

    public Handshake() { }
}
