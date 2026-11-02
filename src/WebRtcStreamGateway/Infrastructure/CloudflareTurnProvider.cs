using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRtcStreamGateway.Domain;

namespace WebRtcStreamGateway.Infrastructure;

public sealed class CloudflareTurnProvider(
    HttpClient httpClient,
    IOptions<CloudflareOptions> options,
    ILogger<CloudflareTurnProvider> logger,
    TimeProvider? timeProvider = null) : IIceServerProvider
{
    private const string DefaultStunUrl = "stun:stun.cloudflare.com:3478";
    private readonly CloudflareOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private IReadOnlyList<IceServer>? _cachedServers;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<IceServer>> GetIceServersAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Cloudflare TURN is disabled in configuration. Using fallback ICE servers.");
            return GetFallbackIceServers();
        }

        if (string.IsNullOrWhiteSpace(_options.TurnKeyId) || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            logger.LogWarning("Cloudflare TURN Key ID or API Token is unconfigured. Using fallback ICE servers.");
            return GetFallbackIceServers();
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_cachedServers != null && now < _cacheExpiresAt)
            return _cachedServers;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedServers != null && now < _cacheExpiresAt)
                return _cachedServers;

            IReadOnlyList<IceServer>? freshServers = await FetchCloudflareCredentialsAsync(cancellationToken);
            if (freshServers != null && freshServers.Count > 0)
            {
                _cachedServers = freshServers;
                int ttlSeconds = Math.Clamp(_options.TtlSeconds, 60, 172800);
                double cacheLifetimeSeconds = ttlSeconds * 0.8;
                _cacheExpiresAt = now.AddSeconds(cacheLifetimeSeconds);
                return _cachedServers;
            }

            return GetFallbackIceServers();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<IceServer>?> FetchCloudflareCredentialsAsync(CancellationToken cancellationToken)
    {
        string url = $"https://rtc.live.cloudflare.com/v1/turn/keys/{_options.TurnKeyId}/credentials/generate";
        int ttlSeconds = Math.Clamp(_options.TtlSeconds, 60, 172800);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            string requestJson = JsonSerializer.Serialize(new { ttl = ttlSeconds });
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Cloudflare Calls TURN API returned HTTP {StatusCode}. Falling back to configured fallback ICE servers.",
                    (int)response.StatusCode);
                return null;
            }

            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseCloudflareResponse(responseContent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Error contacting Cloudflare Calls TURN API. Falling back to configured fallback ICE servers.");
            return null;
        }
    }

    private IReadOnlyList<IceServer> ParseCloudflareResponse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("iceServers", out JsonElement iceServersElement))
        {
            logger.LogWarning("Cloudflare Calls TURN API response missing 'iceServers' property.");
            return GetFallbackIceServers();
        }

        List<IceServer> parsedList = [];

        if (iceServersElement.ValueKind == JsonValueKind.Object)
        {
            IceServer? server = ParseSingleIceServer(iceServersElement);
            if (server != null)
                parsedList.Add(server);
        }
        else if (iceServersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in iceServersElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                IceServer? server = ParseSingleIceServer(element);
                if (server != null)
                    parsedList.Add(server);
            }
        }

        if (parsedList.Count == 0)
        {
            logger.LogWarning("No valid ICE servers parsed from Cloudflare response.");
            return GetFallbackIceServers();
        }

        return parsedList;
    }

    private static IceServer? ParseSingleIceServer(JsonElement element)
    {
        List<string> urls = [];

        if (element.TryGetProperty("urls", out JsonElement urlsElement))
        {
            if (urlsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement urlItem in urlsElement.EnumerateArray())
                {
                    string? u = urlItem.GetString();
                    if (!string.IsNullOrWhiteSpace(u))
                        urls.Add(u.Trim());
                }
            }
            else if (urlsElement.ValueKind == JsonValueKind.String)
            {
                string? u = urlsElement.GetString();
                if (!string.IsNullOrWhiteSpace(u))
                {
                    string[] split = u.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    urls.AddRange(split);
                }
            }
        }
        else if (element.TryGetProperty("url", out JsonElement singleUrlElement) &&
                 singleUrlElement.ValueKind == JsonValueKind.String)
        {
            string? u = singleUrlElement.GetString();
            if (!string.IsNullOrWhiteSpace(u))
                urls.Add(u.Trim());
        }

        if (urls.Count == 0)
            return null;

        string? username = null;
        if (element.TryGetProperty("username", out JsonElement usernameElement) &&
            usernameElement.ValueKind == JsonValueKind.String)
            username = usernameElement.GetString()?.Trim();

        string? credential = null;
        if (element.TryGetProperty("credential", out JsonElement credentialElement) &&
            credentialElement.ValueKind == JsonValueKind.String)
            credential = credentialElement.GetString()?.Trim();

        return new IceServer(urls, username, credential);
    }

    private IReadOnlyList<IceServer> GetFallbackIceServers()
    {
        if (_options.FallbackIceServers != null && _options.FallbackIceServers.Count > 0)
            return _options.FallbackIceServers;

        return [new IceServer([DefaultStunUrl])];
    }
}
