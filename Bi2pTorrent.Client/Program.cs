using Bi2pTorrent.Client;

using DotI2p;

using System.Net;

Console.WriteLine("Bi2pTorrent - A torrent client for the invisible internet");

var torrentRepository = new TorrentRepository(@"C:\Projects\Personal\Bi2pTorrent\torrents");
torrentRepository.Initialize();

var fileManager = new FileManager(@"C:\Projects\Personal\Bi2pTorrent\downloads");
fileManager.Initialize();

foreach (var torrent in torrentRepository.Torrents)
{
    await fileManager.EnsureFilesAsync(torrent);
}

SamConnection samConnection;

if (args.Length > 0)
{
    samConnection = new SamConnection(IPAddress.Parse(args[0]));
}
else
{
    samConnection = new SamConnection();
}

await samConnection.ConnectAsync();

//var trackerSession = new SamSession(samConnection);
//var trackerDestination = await trackerSession.CreateStreamAsync();

//var trackerServer = new TrackerServer(trackerSession);
//_ = trackerServer.StartAsync();

var protocolSession = new SamSession(samConnection);
var protocolDestination = await protocolSession.CreateStreamAsync();

Console.WriteLine($"Protocol destination: {protocolDestination.GetB32Hostname()}");

var myPeerId = "Bi2p-0.1.0" + protocolSession.Destination!.GetB32Hostname()[..10];
var announceClient = new AnnounceClient(protocolSession, myPeerId);

var connectionManagers = new List<ConnectionManager>();

foreach (var torrent in torrentRepository.Torrents)
{
    var torrentState = new TorrentState(torrent);
    await fileManager.ScanPiecesAsync(torrentState);

    Console.WriteLine($"{torrent.DisplayName}: {torrentState.Bitfield.CompletedPieceCount}/{torrentState.Torrent.NumberOfPieces} pieces = {torrentState.Bitfield.CompletedPieceCount * 100.0 / torrentState.Torrent.NumberOfPieces:N1}% completed.");

    var response = await announceClient.SendAnnounce(torrentState);

    if (response.FailureReason != null)
    {
        Console.WriteLine($"Announce failed: {response.FailureReason}");
    }
    else
    {
        Console.WriteLine($"{torrent.DisplayName}: Complete: {response.Complete}, Incomplete: {response.Incomplete}, Interval: {response.Interval}, Peers: {response.Peers.Count}");
        
        var torrentManager = new TorrentManager(torrentState, fileManager);
        var connectionManager = new ConnectionManager(protocolSession, myPeerId, torrent, torrentManager, torrentState);
        torrentManager.SetConnectionManager(connectionManager);
        connectionManagers.Add(connectionManager);

        connectionManager.AddDiscoveredPeers(response.Peers.Select(p => p.Address).ToArray());

        await connectionManager.StartAsync();
    }
}

_ = new PeerListener(protocolSession, connectionManagers.ToArray()).StartAsync();

Console.ReadLine();