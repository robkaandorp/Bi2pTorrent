using BencodeNET.Objects;
using BencodeNET.Parsing;

using Multiformats.Base;

using System.Text;

namespace Bi2pTorrent.Client.Protocol.ExtensionProtocol;

public class ExtendedMessageFactory
{
    public static IExtendedMessage Create(ReadOnlyMemory<byte> data)
    {
        var extendedMessageId = data.Span[0];

        switch (extendedMessageId)
        {
            case 0:
                var handshakeMessage = new HandshakeMessage();
                var parser = new BencodeParser();
                var result = parser.Parse(data.Slice(1).ToArray());

                if (result is BDictionary handshakeDict)
                {
                    if (handshakeDict.TryGetValue("v", out var version))
                    {
                        handshakeMessage.Version = version?.ToString() ?? string.Empty;
                    }

                    if (handshakeDict.TryGetValue("reqq", out var reqq))
                    {
                        handshakeMessage.Reqq = (int)((BNumber)reqq).Value;
                    }

                    if (handshakeDict.TryGetValue("metadata_size", out var metadataSize))
                    {
                        handshakeMessage.MetadataSize = (int)((BNumber)metadataSize).Value;
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

            case 1:
                var i2pPexMessage = new I2pPexMessage();
                var parser2 = new BencodeParser(Encoding.Latin1);
                var result2 = parser2.Parse(data.Slice(1).ToArray());

                if (result2 is BDictionary pexDict)
                {
                    if (pexDict.TryGetValue("added", out var added) && added is BString addedBString)
                    {
                        i2pPexMessage.AddedPeers = addedBString.Value.ToArray()
                            .Chunk(32)
                            .Select(chunk => $"{Multibase.Encode(MultibaseEncoding.Base32Lower, chunk)[1..]}.b32.i2p")
                            .ToList();
                    }

                    if (pexDict.TryGetValue("added.f", out var addedFlags) && addedFlags is BString addedFlagsBString)
                    {
                        i2pPexMessage.AddedPeersFlags = addedFlagsBString.Value.ToArray().Select(b => "0x" + b.ToString("X2")).ToList();
                    }

                    if (pexDict.TryGetValue("dropped", out var dropped) && dropped is BString droppedBString)
                    {
                        i2pPexMessage.DroppedPeers = droppedBString.Value.ToArray()
                            .Chunk(32)
                            .Select(chunk => $"{Multibase.Encode(MultibaseEncoding.Base32Lower, chunk)[1..]}.b32.i2p")
                            .ToList();
                    }
                }

                return i2pPexMessage;

            default:
                return new UnknownExtendedMessage(extendedMessageId, data.Slice(1).Length);
        }
    }
}
