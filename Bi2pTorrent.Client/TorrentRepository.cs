using BencodeNET.Parsing;
using BencodeNET.Torrents;

namespace Bi2pTorrent.Client;

public class TorrentRepository(string folder)
{
    BencodeParser parser = new BencodeParser();

    List<Torrent> torrents = [];

    public void Initialize()
    {
        var files = Directory.GetFiles(folder, "*.torrent");

        foreach (var file in files)
        {
            var torrent = parser.Parse<Torrent>(file);
            this.torrents.Add(torrent);

            Console.WriteLine($"Loaded torrent: {torrent.DisplayName}, {(torrent.FileMode == TorrentFileMode.Single ? 1 : torrent.Files.Count)} files, trackers: {string.Join(", ", torrent.Trackers.SelectMany(t => t))}");
        }
    }

    public IReadOnlyList<Torrent> Torrents { get => this.torrents.AsReadOnly(); }
}
