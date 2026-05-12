namespace Bi2pTorrent.Client.Protocol.ExtensionProtocol;

public interface IExtendedMessage
{
    byte ExtendedMessageId { get; }
}

public class HandshakeMessage : IExtendedMessage
{
    public byte ExtendedMessageId => 0;

    public string Version { get; set; } = string.Empty;

    public Dictionary<string, byte> SupportedExtensions { get; set; } = [];
}

public record UnknownExtendedMessage(byte ExtendedMessageId, int Length) : IExtendedMessage;
