using BencodeNET.Objects;

using Bi2pTorrent.Client.Extensions;

using Multiformats.Base;

namespace Bi2pTorrent.Client.Protocol.ExtensionProtocol;

public interface IExtendedMessage
{
    byte ExtendedMessageId { get; }
}

public class HandshakeMessage : IExtendedMessage
{
    public byte ExtendedMessageId => 0;

    // v
    public string Version { get; set; } = string.Empty;

    // reqq
    public int Reqq { get; set; }

    // metadata_size
    public int MetadataSize { get; set; }

    // m
    public Dictionary<string, byte> SupportedExtensions { get; set; } = [];

    public byte[] EncodeAsBytes()
    {
        return new BDictionary
        {
            { "v", Version },
            { "reqq", Reqq },
            { "metadata_size", MetadataSize },
            { "m", new BDictionary(SupportedExtensions.Select(kvp => new KeyValuePair<BString, IBObject>(kvp.Key, new BNumber(kvp.Value)))) }
        }.EncodeAsBytes();
    }
}

public class I2pPexMessage : IExtendedMessage
{
    public byte ExtendedMessageId { get; set; } = 1;

    public List<string> AddedPeers { get; set; } = [];
    
    public List<string> AddedPeersFlags { get; set; } = [];

    public List<string> DroppedPeers { get; set; } = [];

    public byte[] EncodeAsBytes()
    {
        var dict = new BDictionary();

        if (AddedPeers.Count > 0)
        {
            dict["added"] = new BString(AddedPeers.SelectMany(peer => peer.Split(".b32.i2p")[0].FromB32AddressToBytes()).ToArray());
        }

        if (AddedPeersFlags.Count > 0)
        {
            dict["added.f"] = new BString(AddedPeersFlags.Select(b => Convert.ToByte(b, 16)).ToArray());
        }

        if (DroppedPeers.Count > 0)
        {
            dict["dropped"] = new BString(DroppedPeers.SelectMany(peer => peer.Split(".b32.i2p")[0].FromB32AddressToBytes()).ToArray());
        }

        return dict.EncodeAsBytes();
    }
}

public record UnknownExtendedMessage(byte ExtendedMessageId, int Length) : IExtendedMessage;
