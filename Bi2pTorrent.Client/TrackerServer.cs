using DotI2p;

using System.Text;

namespace Bi2pTorrent.Client;

public class TrackerServer(SamSession samSession)
{
    public async Task StartAsync()
    {
        if (samSession.Destination == null)
        {
            throw new InvalidOperationException("SAM session must have a destination to start the tracker server.");
        }

        var serverHostname = samSession.Destination.GetB32Hostname();
        Console.WriteLine($"TrackerServer destination: {serverHostname}");

        while (true)
        {
            using var trackerVirtualStream = samSession.CreateVirtualStream();
            var acceptedConnection = await trackerVirtualStream.AcceptAsync();

            using var reader = new StreamReader(acceptedConnection.TcpClient.GetStream());
            bool request = true;

            while (true)
            {
                var line = await reader.ReadLineAsync();

                if (request)
                {
                    Console.Write($"{acceptedConnection.Destination.GetB32Hostname()}: ");
                    Console.WriteLine(line);
                    request = false;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    await Console.Out.FlushAsync();
                    break;
                }
            }

            using var writer = new StreamWriter(acceptedConnection.TcpClient.GetStream());

            string body = $"""
                Your address: {acceptedConnection.Destination.GetB32Hostname()}
                My address: {serverHostname}
                """;

            string header = $"""
                HTTP/1.1 200 OK
                Content-Type: text/plain
                Content-Length: {Encoding.UTF8.GetByteCount(body)}
                Connection: close

                """;

            writer.WriteLine(header);
            writer.Write(body);
            writer.Close();
        }
    }
}
