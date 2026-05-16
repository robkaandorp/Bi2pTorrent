using Bi2pTorrent.Client;
using Bi2pTorrent.Client.Protocol;

using DotI2p;

using System.Net;
using System.Reflection;

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

var primarySession = new SamSession(samConnection);
var destination = await primarySession.CreatePrimarySessionAsync();
var streamSubSession = await primarySession.CreateStreamSubSession();

Console.WriteLine($"Protocol destination: {destination!.GetB32Hostname()}");

var version = Assembly.GetEntryAssembly()?.GetName().Version;
var myPeerId = $"Bi2p-{version?.Major ?? 0:X}.{version?.Minor ?? 0:X}.{version?.Build ?? 0:X}" + destination.GetB32Hostname()[..10];

var connectionManagers = new List<ConnectionManager>();
var announceClient = new HttpAnnounceClient(destination, streamSubSession, myPeerId);
var trackerManager = new TrackerManager(announceClient);
await trackerManager.LoadTrackersAsync(@"C:\Projects\Personal\Bi2pTorrent\Bi2pTorrent.Client\trackers.txt");
await trackerManager.StartAsync();

foreach (var torrent in torrentRepository.Torrents)
{
    var infoHash = new InfoHash(torrent.GetInfoHashBytes());

    var torrentState = new TorrentState(torrent);
    await fileManager.ScanPiecesAsync(torrentState);
    torrentState.Start();

    Console.WriteLine($"{torrent.DisplayName}: {torrentState.Bitfield.CompletedPieceCount}/{torrentState.Torrent.NumberOfPieces} pieces = {torrentState.Bitfield.CompletedPieceCount * 100.0 / torrentState.Torrent.NumberOfPieces:N1}% completed.");

    var torrentManager = new TorrentManager(torrentState, fileManager);
    var connectionManager = new ConnectionManager(streamSubSession, destination, myPeerId, torrent, torrentManager, torrentState);
    torrentManager.SetConnectionManager(connectionManager);
    connectionManagers.Add(connectionManager);

    await connectionManager.StartAsync();
    trackerManager.AddInfoHash(infoHash, torrent.IsPrivate, [.. torrent.Trackers.SelectMany(t => t)], torrentState.StatsRequest, connectionManager.AddDiscoveredPeers);
}

_ = new PeerListener(streamSubSession, [.. connectionManagers]).StartAsync();

Console.ReadLine();