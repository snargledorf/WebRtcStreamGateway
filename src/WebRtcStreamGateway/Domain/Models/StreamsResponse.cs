namespace WebRtcStreamGateway.Domain;

public sealed record StreamsResponse(
    IReadOnlyList<string> Streams,
    string DefaultStream);