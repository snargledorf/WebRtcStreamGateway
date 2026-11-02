namespace WebRtcStreamGateway.Domain;

public sealed record IceServer(
    IReadOnlyList<string> Urls,
    string? Username = null,
    string? Credential = null);