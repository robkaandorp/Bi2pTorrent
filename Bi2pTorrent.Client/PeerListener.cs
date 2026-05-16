using Bi2pTorrent.Client.Protocol;

using DotI2p;

namespace Bi2pTorrent.Client;

public class PeerListener(SamStreamSubSession streamSubSession, ConnectionManager[] connectionManagers)
{
    public async Task StartAsync()
    {
        _ = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                Console.WriteLine($"Started listening for incoming connections...");

                var virtualStream = streamSubSession.CreateVirtualStream();
                var acceptedConnection = await virtualStream.AcceptAsync();

                var peer = new Peer(acceptedConnection.Destination.GetB32Hostname());

                Console.WriteLine($"Accepted connection from {peer.Address}");

                Handshake handshake;

                try
                {
                    var stream = acceptedConnection.TcpClient.GetStream();
                    handshake = await Handshake.FromStreamAsync(stream);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{peer.Address} - Connection failed: {ex.Message}");
                    virtualStream.Dispose();
                    continue;
                }

                var connectionManager = connectionManagers.FirstOrDefault(cm => cm.Torrent.GetInfoHashBytes().SequenceEqual(handshake.InfoHash));

                if (connectionManager == null)
                {
                    Console.WriteLine($"{peer.Address} - No matching torrent for info hash {handshake.InfoHash}");
                    virtualStream.Dispose();
                    continue;
                }

                if (!await connectionManager.TryAddPeerFromListener(peer, acceptedConnection, handshake))
                {
                    virtualStream.Dispose();
                }
            }
        }, TaskCreationOptions.LongRunning);
    }
}
