namespace WebRtcStreamGateway.Domain;

public sealed record GatewayHealth(
    string Status,
    bool BackendReachable,
    bool CloudflareConfigured,
    DateTimeOffset CheckedAt);