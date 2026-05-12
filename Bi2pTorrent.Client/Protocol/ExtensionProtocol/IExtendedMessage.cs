using BencodeNET.Objects;

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
    public byte ExtendedMessageId => 1;

    public List<string> AddedPeers { get; set; } = [];
    
    public List<string> AddedPeersFlags { get; set; } = [];

    public List<string> DroppedPeers { get; set; } = [];
}

public record UnknownExtendedMessage(byte ExtendedMessageId, int Length) : IExtendedMessage;
