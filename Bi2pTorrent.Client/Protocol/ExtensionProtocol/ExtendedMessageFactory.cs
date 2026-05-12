using BencodeNET.Objects;
using BencodeNET.Parsing;

namespace Bi2pTorrent.Client.Protocol.ExtensionProtocol;

public class ExtendedMessageFactory
{
    private static BencodeParser parser = new BencodeParser();

    public static IExtendedMessage Create(ReadOnlyMemory<byte> data)
    {
        var extendedMessageId = data.Span[0];

        switch (extendedMessageId)
        {
            case 0:
                var handshakeMessage = new HandshakeMessage();
                var result = parser.Parse(data.Slice(1).ToArray());

                if (result is BDictionary handshakeDict)
                {
                    if (handshakeDict.TryGetValue("v", out var version))
                    {
                        handshakeMessage.Version = version?.ToString() ?? string.Empty;
                    }

                    if (handshakeDict.TryGetValue("m", out var m))
                    {
                        if (m is BDictionary mDict)
                        {
                            foreach (var kvp in mDict)
                            {
                                handshakeMessage.SupportedExtensions[kvp.Key.ToString()] = (byte)((BNumber)kvp.Value).Value;
                            }
                        }
                    }
                }

                return handshakeMessage;

            default:
                return new UnknownExtendedMessage(extendedMessageId, data.Slice(1).Length);
        }
    }
}
