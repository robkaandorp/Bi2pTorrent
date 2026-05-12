using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Bi2pTorrent.Client.Protocol;

public class MessageFactory
{
    public static IMessage Create(ReadOnlyMemory<byte> data)
    {
        var type = data.Span[0];

        switch (type)
        {
            case 0:
                return new ChokeMessage();

            case 1:
                return new UnchokeMessage();

            case 2:
                return new InterestedMessage();

            case 3:
                return new NotInterestedMessage();

            case 4:
                return new HaveMessage() { PieceIndex = BinaryPrimitives.ReadInt32BigEndian(data[1..5].Span) };

            case 5:
                return new BitfieldMessage(data[1..].ToArray());

            case 6:
                return new RequestMessage(
                    BinaryPrimitives.ReadInt32BigEndian(data[1..5].Span),
                    BinaryPrimitives.ReadInt32BigEndian(data[5..9].Span),
                    BinaryPrimitives.ReadInt32BigEndian(data[9..13].Span));

            case 7:
                var pieceMessage = new PieceMessage(
                    BinaryPrimitives.ReadInt32BigEndian(data[1..5].Span),
                    BinaryPrimitives.ReadInt32BigEndian(data[5..9].Span),
                    data.Length - 9);
                pieceMessage.SetData(data.Slice(9));
                return pieceMessage;

            case 8:
                return new CancelMessage(
                    BinaryPrimitives.ReadInt32BigEndian(data[1..5].Span),
                    BinaryPrimitives.ReadInt32BigEndian(data[5..9].Span),
                    BinaryPrimitives.ReadInt32BigEndian(data[9..13].Span));

            default:
                return new UnknownMessage(type, data.Slice(1).Length);
        }
    }
}
