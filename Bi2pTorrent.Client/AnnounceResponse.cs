namespace Bi2pTorrent.Client;

public record AnnounceResponse(long Complete, long Incomplete, long Interval, IList<Peer> Peers, string? FailureReason = null);
