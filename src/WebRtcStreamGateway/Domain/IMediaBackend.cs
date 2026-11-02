namespace WebRtcStreamGateway.Domain;

public interface IMediaBackend
{
    Task<SdpAnswer> NegotiateOfferAsync(string streamName, string sdpOffer, CancellationToken cancellationToken = default);
    Task<byte[]> CaptureSnapshotAsync(string streamName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAvailableStreamsAsync(CancellationToken cancellationToken = default);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}