namespace WebRtcStreamGateway.Domain;

public sealed record SdpAnswer(
    string Sdp,
    string Type = "answer");