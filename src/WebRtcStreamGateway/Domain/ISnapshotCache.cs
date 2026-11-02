namespace WebRtcStreamGateway.Domain;

public interface ISnapshotCache
{
    bool TryGet(string streamName, out byte[]? frameData);
    void Set(string streamName, byte[] frameData, TimeSpan? customTtl = null);
    void Invalidate(string? streamName = null);
}