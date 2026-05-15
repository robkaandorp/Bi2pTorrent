using Bi2pTorrent.Client.Protocol;

namespace Bi2pTorrent.Client;

public class TrackerManager(AnnounceClient announceClient)
{
    private class TrackerState(InfoHash infoHash, bool isPrivate, string[] trackers, Func<TorrentStats> statsRequest, Action<Peer[]> discoveredPeersCallback)
    {
        public InfoHash InfoHash { get; init; } = infoHash;

        public bool IsPrivate { get; init; } = isPrivate;

        public string[] Trackers { get; init; } = trackers;

        public Func<TorrentStats> StatsRequest { get; init; } = statsRequest;

        public Action<Peer[]> DiscoveredPeersCallback { get; init; } = discoveredPeersCallback;

        public Dictionary<string, AnnounceResponse> TrackerResponses { get; } = [];

        public bool Started { get; set; } = false;
    }

    private readonly List<string> trackerUrls = [];

    private readonly Dictionary<string, TrackerState> trackerStateDictionary = [];

    public async Task LoadTrackersAsync(string filename)
    {
        if (!File.Exists(filename))
        {
            throw new Exception($"Tracker file {filename} does not exist.");
        }

        this.trackerUrls.AddRange(await File.ReadAllLinesAsync(filename));
    }

    public async Task StartAsync()
    {
        _ = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                string[] infoHashes;

                lock (this.trackerStateDictionary)
                {
                    infoHashes = this.trackerStateDictionary.Keys.ToArray();
                }

                foreach (var infoHash in infoHashes)
                {
                    await this.AnnounceAsync(infoHash);
                }

                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        });
    }

    public void AddInfoHash(InfoHash infoHash, bool isPrivate, string[] trackers, Func<TorrentStats> statsRequest, Action<Peer[]> discoveredPeersCallback)
    {
        ArgumentNullException.ThrowIfNull(infoHash);
        ArgumentNullException.ThrowIfNull(statsRequest);
        ArgumentNullException.ThrowIfNull(discoveredPeersCallback);

        var infoHashString = infoHash.GetHexString();

        if (this.trackerStateDictionary.ContainsKey(infoHashString))
        {
            throw new Exception($"InfoHash {infoHashString} is already being tracked.");
        }

        List<string> validTrackers = [.. trackers];

        if (!isPrivate)
        {
            validTrackers.AddRange(this.trackerUrls);
        }

        var trackerState = new TrackerState(infoHash, isPrivate, validTrackers.ToArray(), statsRequest, discoveredPeersCallback);

        lock (this.trackerStateDictionary)
        {
            this.trackerStateDictionary[infoHashString] = trackerState;
        }
    }

    private async Task AnnounceAsync(string infoHash)
    {
        TrackerState trackerState;

        lock (this.trackerStateDictionary)
        {
            trackerState = this.trackerStateDictionary[infoHash];
        }

        var stats = trackerState.StatsRequest();
        var count = 0;

        foreach (var tracker in trackerState.Trackers)
        {
            AnnounceEvent announceEvent = AnnounceEvent.None;

            if (trackerState.TrackerResponses.TryGetValue(tracker, out var previousResponse))
            {
                if (previousResponse.LastUpdate > DateTime.UtcNow.AddSeconds(-previousResponse.Interval))
                {
                    continue;
                }

                if (!trackerState.Started)
                {
                    announceEvent = AnnounceEvent.Started;
                }
            }
            else
            {
                announceEvent = AnnounceEvent.Started;
            }

            try
            {
                var response = await announceClient.SendAnnounceAsync(tracker, trackerState.InfoHash, stats, announceEvent);
                trackerState.DiscoveredPeersCallback(response.Peers.ToArray());
                trackerState.TrackerResponses[tracker] = response;
                count++;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error announcing {infoHash} to tracker {tracker}: {e.Message}");
                trackerState.TrackerResponses[tracker] = new AnnounceResponse(0, 0, 600, [], DateTime.UtcNow, e.Message);
            }
        }

        if (!trackerState.Started && count > 0)
        {
            Console.WriteLine($"Torrent {infoHash} started on {count} trackers.");
            trackerState.Started = true;
        }
    }
}