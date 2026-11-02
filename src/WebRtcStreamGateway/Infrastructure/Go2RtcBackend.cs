using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRtcStreamGateway.Domain;

namespace WebRtcStreamGateway.Infrastructure;

public sealed class Go2RtcBackend(
    HttpClient httpClient,
    IOptions<Go2RtcOptions> options,
    ILogger<Go2RtcBackend> logger) : IMediaBackend
{
    private readonly Go2RtcOptions _options = options.Value;

    public async Task<SdpAnswer> NegotiateOfferAsync(
        string streamName,
        string sdpOffer,
        CancellationToken cancellationToken = default)
    {
        string stream = string.IsNullOrWhiteSpace(streamName) ? _options.DefaultStream : streamName.Trim();
        string baseUrl = _options.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/webrtc?src={Uri.EscapeDataString(stream)}";

        logger.LogInformation("Proxying WebRTC SDP offer to go2rtc for stream '{Stream}' at {Url}", stream, url);

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Content = new StringContent(sdpOffer, Encoding.UTF8, "application/sdp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sdp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "go2rtc returned error HTTP {StatusCode} during SDP negotiation: {Error}",
                (int)response.StatusCode,
                errorBody);
            throw new HttpRequestException($"go2rtc returned HTTP {(int)response.StatusCode}: {errorBody}");
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        string answerSdp = responseBody;

        if (responseBody.TrimStart().StartsWith('{'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("sdp", out JsonElement sdpProp) &&
                    sdpProp.ValueKind == JsonValueKind.String)
                {
                    string? parsedSdp = sdpProp.GetString();
                    if (!string.IsNullOrWhiteSpace(parsedSdp))
                        answerSdp = parsedSdp;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Failed to parse go2rtc response as JSON; treating as raw SDP string.");
            }
        }

        return new SdpAnswer(answerSdp, "answer");
    }

    public async Task<byte[]> CaptureSnapshotAsync(
        string streamName,
        CancellationToken cancellationToken = default)
    {
        string stream = string.IsNullOrWhiteSpace(streamName) ? _options.DefaultStream : streamName.Trim();
        string baseUrl = _options.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/frame.jpeg?src={Uri.EscapeDataString(stream)}";

        logger.LogDebug("Capturing snapshot from go2rtc stream '{Stream}' at {Url}", stream, url);

        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "go2rtc returned HTTP {StatusCode} while capturing snapshot for stream '{Stream}': {Error}",
                (int)response.StatusCode,
                stream,
                errorBody);
            throw new HttpRequestException($"Failed to capture snapshot from go2rtc: HTTP {(int)response.StatusCode}");
        }

        byte[] imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return imageBytes;
    }

    public async Task<IReadOnlyList<string>> GetAvailableStreamsAsync(CancellationToken cancellationToken = default)
    {
        string baseUrl = _options.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/streams";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("go2rtc returned HTTP {StatusCode} querying streams at {Url}", (int)response.StatusCode, url);
                return [];
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            List<string> streamList = [];
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                streamList.Add(property.Name);

            streamList.Sort(StringComparer.OrdinalIgnoreCase);
            return streamList;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query streams from go2rtc at {Url}", url);
            return [];
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string baseUrl = _options.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/streams";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Health check probe to go2rtc failed at {Url}", url);
            return false;
        }
    }
}
