namespace WebRtcStreamGateway.Domain;

public sealed record WebRtcOfferRequest(
    string Sdp,
    string Type = "offer",
    string? Stream = null);