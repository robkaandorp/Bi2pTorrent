using BencodeNET.Objects;
using BencodeNET.Parsing;

using Bi2pTorrent.Client.Protocol;

using DotI2p;

using Multiformats.Base;

using System.Text;

namespace Bi2pTorrent.Client;

public enum AnnounceEvent
{
    None,
    Started,
    Stopped,
    Completed
}

public class HttpAnnounceClient(DestinationKey destination, SamSubSession samSubSession, string myPeerId)
{
    // https://www.bittorrent.org/beps/bep_0003.html
    public async Task<AnnounceResponse> SendAnnounceAsync(string tracker, InfoHash infoHash, TorrentStats torrentStats, AnnounceEvent announceEvent = AnnounceEvent.None)
    {
        var trackerUri = new UriBuilder(tracker);

        using var virtualStream = samSubSession.CreateVirtualStream();
        var tcpClient = await virtualStream.ConnectAsync(new DestinationKey(trackerUri.Host));

        using var stream = tcpClient.GetStream();
        using var writer = new StreamWriter(stream);

        var eventString = announceEvent switch
        {
            AnnounceEvent.Started => "&event=started",
            AnnounceEvent.Stopped => "&event=stopped",
            AnnounceEvent.Completed => "&event=completed",
            _ => ""
        };

        string request = $"""
            GET {trackerUri.Path}?info_hash={infoHash.GetUriString()}&peer_id={myPeerId}&port=6881&uploaded={torrentStats.Uploaded}&downloaded={torrentStats.Downloaded}&left={torrentStats.Remaining}&compact=1&ip={destination.GetB32Hostname()}{eventString} HTTP/1.1
            Host: {trackerUri.Host}
            Connection: close
            
            """;

        await writer.WriteLineAsync(request);
        await writer.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.Latin1);
        var responseLine = await reader.ReadLineAsync();

        if (responseLine == null || !responseLine.Equals("HTTP/1.1 200 OK"))
        {
            throw new Exception($"Unexpected response from tracker: {responseLine}");
        }

        int contentLength = 0;

        while (true)
        {
            var responseHeaderLine = await reader.ReadLineAsync();

            if (string.IsNullOrEmpty(responseHeaderLine))
            {
                break;
            }

            if (responseHeaderLine.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
            {
                var contentLengthString = responseHeaderLine.Substring("Content-Length: ".Length);

                if (!int.TryParse(contentLengthString, out contentLength))
                {
                    throw new Exception($"Invalid Content-Length header: {contentLengthString}");
                }
            }
        }

        //if (contentLength <= 0)
        //{
        //    throw new Exception("Content-Length header is missing or invalid.");
        //}

        if (contentLength > 1024 * 1024)
        {
            throw new Exception("Content-Length is too large.");
        }

        var body = (await reader.ReadToEndAsync())
            .Split("\r\n")
            .Last(line => line.StartsWith('d'));

        var parser = new BencodeParser();
        var result = parser.Parse(Encoding.Latin1.GetBytes(body));

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

            return new AnnounceResponse(complete, incomplete, interval, peers, DateTime.UtcNow, failureReason);
        }

        throw new InvalidOperationException("Unexpected response from tracker.");
    }
}
