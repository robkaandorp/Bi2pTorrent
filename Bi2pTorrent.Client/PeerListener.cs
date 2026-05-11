using Bi2pTorrent.Client.Protocol;

using DotI2p;

namespace Bi2pTorrent.Client;

public class PeerListener(SamSession protocolSession, ConnectionManager[] connectionManagers)
{
    public async Task StartAsync()
    {
        _ = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                Console.WriteLine($"Started listening for incoming connections...");

                var virtualStream = protocolSession.CreateVirtualStream();
                var acceptedConnection = await virtualStream.AcceptAsync();

                var peer = new Peer(acceptedConnection.Destination.GetB32Hostname());

                Console.WriteLine($"Accepted connection from {peer.Address}");

                Handshake handshake;

                try
                {
                    handshake = Handshake.FromStream(acceptedConnection.TcpClient.GetStream());
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

                await connectionManager.AddPeerFromListener(peer, acceptedConnection, handshake);
            }
        }, TaskCreationOptions.LongRunning);
    }
}
