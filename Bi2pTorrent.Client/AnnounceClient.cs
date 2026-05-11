using BencodeNET.Objects;
using BencodeNET.Parsing;

using DotI2p;

using Multiformats.Base;

namespace Bi2pTorrent.Client;

public class AnnounceClient(SamSession samSession, string myPeerId)
{
    // https://www.bittorrent.org/beps/bep_0003.html
    public async Task<AnnounceResponse> SendAnnounce(TorrentState torrentState)
    {
        if (samSession.Destination == null)
        {
            throw new InvalidOperationException("SAM session must have a destination to send an announce.");
        }

        var myHostname = samSession.Destination.GetB32Hostname();
        //var firstTracker = new UriBuilder(torrentState.Torrent.Trackers.First().First());
        var firstTracker = new UriBuilder("http://opentracker.dg2.i2p/announce.php");   // TODO First try the trackers from the torrent, then fall back to a hardcoded one if they fail. This is just for testing.

        using var virtualStream = samSession.CreateVirtualStream();
        var tcpClient = await virtualStream.ConnectAsync(new DestinationKey(firstTracker.Host));

        var writer = new StreamWriter(tcpClient.GetStream());

        string request = $"""
            GET {firstTracker.Path}?info_hash=%{torrentState.Torrent.GetInfoHashBytes().Select(b => b.ToString("X2")).Aggregate((a, b) => a + "%" + b)}&peer_id={myPeerId}&port=6881&uploaded=0&downloaded=0&left={torrentState.Torrent.TotalSize - torrentState.Bitfield.CompletedPieceCount * torrentState.Torrent.PieceSize}&compact=1&ip={samSession.Destination.Destination}.i2p HTTP/1.1
            Host: {firstTracker.Host}
            Connection: close
            
            """;

        await writer.WriteLineAsync(request);
        await writer.FlushAsync();

        // Read bytes form the BaseStream until the end.
        var stream = tcpClient.GetStream();
        var buffer = new byte[1024];
        var numRead = stream.Read(buffer);
        var responseList = new List<byte>();

        while (numRead > 0)
        {
            responseList.AddRange(buffer.Take(numRead));
            numRead = stream.Read(buffer);
        }

        var response = responseList.ToArray();
        // Find two newlines in the response.
        var endOfHeader = response.IndexOf("\r\n\r\n"u8) + 4;

        var parser = new BencodeParser();
        var result = parser.Parse(response.AsSpan(endOfHeader).ToArray());

        if (result is BDictionary dictionary)
        {
            long complete = 0;
            long incomplete = 0;
            long interval = 3600;
            List<Peer> peers = [];
            string? failureReason = null;

            if (dictionary["complete"] is BNumber completeNumber)
            {
                complete = completeNumber.Value;
            }

            if (dictionary["incomplete"] is BNumber incompleteNumber)
            {
                incomplete = incompleteNumber.Value;
            }

            if (dictionary["interval"] is BNumber intervalNumber)
            {
                interval = intervalNumber.Value;
            }

            if (dictionary["peers"] is BString peersString)
            {
                peers = peersString.Value.ToArray()
                    .Chunk(32)
                    .Select(bytes => $"{Multibase.Encode(MultibaseEncoding.Base32Lower, bytes)[1..]}.b32.i2p")
                    .Select(s => new Peer(s))
                    .ToList();
            }

            if (dictionary["failure reason"] is BString failureReasonString)
            {
                failureReason = failureReasonString.ToString();
            }

            return new AnnounceResponse(complete, incomplete, interval, peers, failureReason);
        }

        throw new InvalidOperationException("Unexpected response from tracker.");
    }
}
