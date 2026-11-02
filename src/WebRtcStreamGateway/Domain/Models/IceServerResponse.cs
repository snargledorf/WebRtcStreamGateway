namespace WebRtcStreamGateway.Domain;

public sealed record IceServerResponse(
    IReadOnlyList<IceServer> IceServers,
    bool ForceTurnRelay = false);