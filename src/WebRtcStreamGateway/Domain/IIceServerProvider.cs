namespace WebRtcStreamGateway.Domain;

public interface IIceServerProvider
{
    Task<IReadOnlyList<IceServer>> GetIceServersAsync(CancellationToken cancellationToken = default);
}