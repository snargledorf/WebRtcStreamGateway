using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WebRtcStreamGateway.Domain;

namespace WebRtcStreamGateway.Infrastructure;

public sealed class SnapshotCache(
    IOptions<SnapshotCacheOptions> options,
    TimeProvider? timeProvider = null) : ISnapshotCache
{
    private sealed record CacheEntry(byte[] Data, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SnapshotCacheOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool TryGet(string streamName, out byte[]? frameData)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            frameData = null;
            return false;
        }

        if (_entries.TryGetValue(streamName, out CacheEntry? entry))
        {
            if (entry.ExpiresAt > _timeProvider.GetUtcNow())
            {
                frameData = entry.Data;
                return true;
            }

            _entries.TryRemove(streamName, out _);
        }

        frameData = null;
        return false;
    }

    public void Set(string streamName, byte[] frameData, TimeSpan? customTtl = null)
    {
        if (string.IsNullOrWhiteSpace(streamName) || frameData == null || frameData.Length == 0)
            return;

        int ttlSeconds = _options.DefaultTtlSeconds > 0 ? _options.DefaultTtlSeconds : 5;
        TimeSpan ttl = customTtl ?? TimeSpan.FromSeconds(ttlSeconds);
        DateTimeOffset expiresAt = _timeProvider.GetUtcNow().Add(ttl);

        _entries[streamName] = new CacheEntry(frameData, expiresAt);
    }

    public void Invalidate(string? streamName = null)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            _entries.Clear();
            return;
        }

        _entries.TryRemove(streamName, out _);
    }
}
