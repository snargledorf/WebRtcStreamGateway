using WebRtcStreamGateway.Domain;

namespace WebRtcStreamGateway.Infrastructure;

public sealed class CloudflareOptions
{
    public const string SectionName = "Cloudflare";

    public string TurnKeyId { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = 3600;
    public bool Enabled { get; set; } = true;
    public List<IceServer> FallbackIceServers { get; set; } = [];
}

public sealed class Go2RtcOptions
{
    public const string SectionName = "Go2Rtc";

    public string BaseUrl { get; set; } = "http://localhost:1984";
    public string DefaultStream { get; set; } = "octocamrtc";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class SnapshotCacheOptions
{
    public const string SectionName = "SnapshotCache";

    public int DefaultTtlSeconds { get; set; } = 5;
}
