using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRtcStreamGateway.Domain;
using WebRtcStreamGateway.Infrastructure;

namespace WebRtcStreamGateway.Tests;

#region Test Helpers

public sealed class FakeTimeProvider(DateTimeOffset initialTime) : TimeProvider
{
    private DateTimeOffset _utcNow = initialTime;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        _utcNow = _utcNow.Add(duration);
    }
}

public sealed class MockHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;

        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return await handlerFunc(request, cancellationToken);
    }
}

public sealed class StubMediaBackend(
    IReadOnlyList<string>? streams = null,
    byte[]? snapshot = null,
    SdpAnswer? sdpAnswer = null,
    bool isHealthy = true,
    bool shouldThrow = false) : IMediaBackend
{
    public Task<SdpAnswer> NegotiateOfferAsync(string streamName, string sdpOffer, CancellationToken cancellationToken = default)
    {
        if (shouldThrow)
            throw new HttpRequestException("Simulated media backend error during negotiation.");

        return Task.FromResult(sdpAnswer ?? new SdpAnswer("v=0\r\no=stub_answer 1 1 IN IP4 127.0.0.1"));
    }

    public Task<byte[]> CaptureSnapshotAsync(string streamName, CancellationToken cancellationToken = default)
    {
        if (shouldThrow)
            throw new HttpRequestException("Simulated media backend error during snapshot capture.");

        return Task.FromResult(snapshot ?? [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]);
    }

    public Task<IReadOnlyList<string>> GetAvailableStreamsAsync(CancellationToken cancellationToken = default)
    {
        if (shouldThrow)
            throw new HttpRequestException("Simulated media backend error during stream listing.");

        return Task.FromResult(streams ?? ["cam1", "cam2"]);
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (shouldThrow)
            return Task.FromResult(false);

        return Task.FromResult(isHealthy);
    }
}

public sealed class StubIceServerProvider(IReadOnlyList<IceServer>? iceServers = null) : IIceServerProvider
{
    public Task<IReadOnlyList<IceServer>> GetIceServersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IceServer> list = iceServers ??
        [
            new IceServer(["stun:stun.cloudflare.com:3478"]),
            new IceServer(["turn:turn.cloudflare.com:3478?transport=udp"], "ephemeral_user", "ephemeral_pass")
        ];

        return Task.FromResult(list);
    }
}

public sealed class CustomGatewayFactory(
    IMediaBackend? mediaBackend = null,
    IIceServerProvider? iceServerProvider = null,
    Action<CloudflareOptions>? configureCloudflare = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            ServiceDescriptor? mediaBackendDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMediaBackend));
            if (mediaBackendDescriptor != null)
                services.Remove(mediaBackendDescriptor);

            ServiceDescriptor? iceServerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IIceServerProvider));
            if (iceServerDescriptor != null)
                services.Remove(iceServerDescriptor);

            services.AddSingleton(mediaBackend ?? new StubMediaBackend());
            services.AddSingleton(iceServerProvider ?? new StubIceServerProvider());

            if (configureCloudflare != null)
                services.Configure(configureCloudflare);
        });
    }
}

#endregion

#region SnapshotCacheTests

public sealed class SnapshotCacheTests
{
    [Fact]
    public void TryGet_ReturnsTrueAndData_WhenEntryExistsAndNotExpired()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        byte[] originalBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
        cache.Set("test-stream", originalBytes);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        bool found = cache.TryGet("test-stream", out byte[]? cachedBytes);

        Assert.True(found);
        Assert.NotNull(cachedBytes);
        Assert.Equal(originalBytes, cachedBytes);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenCacheIsEmptyOrStreamNotFound()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        bool found = cache.TryGet("non-existent", out byte[]? cachedBytes);

        Assert.False(found);
        Assert.Null(cachedBytes);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenStreamNameIsNullOrEmpty()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        bool foundEmpty = cache.TryGet("", out byte[]? emptyBytes);
        bool foundWhitespace = cache.TryGet("   ", out byte[]? whitespaceBytes);

        Assert.False(foundEmpty);
        Assert.Null(emptyBytes);
        Assert.False(foundWhitespace);
        Assert.Null(whitespaceBytes);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenEntryHasExpired()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 5 });
        SnapshotCache cache = new(options, timeProvider);

        byte[] originalBytes = [1, 2, 3];
        cache.Set("cam1", originalBytes);

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        bool found = cache.TryGet("cam1", out byte[]? cachedBytes);

        Assert.False(found);
        Assert.Null(cachedBytes);
    }

    [Fact]
    public void Set_WithCustomTtl_OverridesDefaultTtl()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 60 });
        SnapshotCache cache = new(options, timeProvider);

        byte[] originalBytes = [1, 2, 3];
        cache.Set("cam1", originalBytes, customTtl: TimeSpan.FromSeconds(2));

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        bool found = cache.TryGet("cam1", out byte[]? cachedBytes);

        Assert.False(found);
        Assert.Null(cachedBytes);
    }

    [Fact]
    public void Set_IgnoresEmptyOrNullData()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        cache.Set("cam1", []);
        bool found = cache.TryGet("cam1", out byte[]? cachedBytes);

        Assert.False(found);
        Assert.Null(cachedBytes);
    }

    [Fact]
    public void Invalidate_SpecificStream_OnlyRemovesTargetStream()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        cache.Set("cam1", [1, 2, 3]);
        cache.Set("cam2", [4, 5, 6]);

        cache.Invalidate("cam1");

        bool foundCam1 = cache.TryGet("cam1", out _);
        bool foundCam2 = cache.TryGet("cam2", out byte[]? cam2Bytes);

        Assert.False(foundCam1);
        Assert.True(foundCam2);
        Assert.NotNull(cam2Bytes);
        Assert.Equal(new byte[] { 4, 5, 6 }, cam2Bytes);
    }

    [Fact]
    public void Invalidate_NullOrEmptyStream_ClearsAllStreams()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 10 });
        SnapshotCache cache = new(options, timeProvider);

        cache.Set("cam1", [1]);
        cache.Set("cam2", [2]);

        cache.Invalidate();

        Assert.False(cache.TryGet("cam1", out _));
        Assert.False(cache.TryGet("cam2", out _));
    }

    [Fact]
    public void ConcurrentAccess_MaintainsThreadSafety()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);
        IOptions<SnapshotCacheOptions> options = Options.Create(new SnapshotCacheOptions { DefaultTtlSeconds = 30 });
        SnapshotCache cache = new(options, timeProvider);

        Parallel.For(0, 100, i =>
        {
            string streamName = $"stream_{i % 5}";
            byte[] data = [(byte)(i & 0xFF)];

            cache.Set(streamName, data);
            cache.TryGet(streamName, out _);

            if (i % 20 == 0)
                cache.Invalidate(streamName);
        });
    }
}

#endregion

#region CloudflareTurnProviderTests

public sealed class CloudflareTurnProviderTests
{
    [Fact]
    public async Task GetIceServersAsync_ReturnsCachedCredentials_WithinEightyPercentTtl()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);

        string cfResponseJson = """
        {
          "iceServers": {
            "urls": ["turn:turn.cloudflare.com:3478?transport=udp"],
            "username": "cached-user",
            "credential": "cached-pass"
          }
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(cfResponseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "test-key-id",
            ApiToken = "test-token",
            TtlSeconds = 1000,
            Enabled = true
        });

        ILogger<CloudflareTurnProvider> logger = NullLogger<CloudflareTurnProvider>.Instance;
        CloudflareTurnProvider provider = new(httpClient, options, logger, timeProvider);

        // First call: triggers upstream HTTP request (call count = 1)
        IReadOnlyList<IceServer> firstResult = await provider.GetIceServersAsync();
        Assert.Equal(1, mockHandler.CallCount);
        Assert.Single(firstResult);
        Assert.Equal("cached-user", firstResult[0].Username);

        // Advance 500 seconds (50% TTL, well within 80% TTL = 800 seconds)
        timeProvider.Advance(TimeSpan.FromSeconds(500));

        // Second call: returns cached result without making upstream HTTP call (call count remains 1)
        IReadOnlyList<IceServer> secondResult = await provider.GetIceServersAsync();
        Assert.Equal(1, mockHandler.CallCount);
        Assert.Single(secondResult);
        Assert.Equal("cached-user", secondResult[0].Username);
    }

    [Fact]
    public async Task GetIceServersAsync_FetchesFreshCredentials_AfterEightyPercentTtlExpires()
    {
        DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(baseTime);

        int invocation = 0;
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            invocation++;
            string json = $$"""
            {
              "iceServers": {
                "urls": ["turn:turn.cloudflare.com:3478?transport=udp"],
                "username": "user-turn-{{invocation}}",
                "credential": "pass-turn-{{invocation}}"
              }
            }
            """;

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "test-key-id",
            ApiToken = "test-token",
            TtlSeconds = 100, // 80% TTL = 80 seconds
            Enabled = true
        });

        ILogger<CloudflareTurnProvider> logger = NullLogger<CloudflareTurnProvider>.Instance;
        CloudflareTurnProvider provider = new(httpClient, options, logger, timeProvider);

        // Call 1 at t0 -> call count 1
        IReadOnlyList<IceServer> firstResult = await provider.GetIceServersAsync();
        Assert.Equal(1, mockHandler.CallCount);
        Assert.Equal("user-turn-1", firstResult[0].Username);

        // Advance 85 seconds (> 80 seconds 80% threshold)
        timeProvider.Advance(TimeSpan.FromSeconds(85));

        // Call 2 at t0 + 85s -> triggers fresh upstream request (call count = 2)
        IReadOnlyList<IceServer> secondResult = await provider.GetIceServersAsync();
        Assert.Equal(2, mockHandler.CallCount);
        Assert.Equal("user-turn-2", secondResult[0].Username);
    }

    [Fact]
    public async Task GetIceServersAsync_NormalizesSingleObjectSchema()
    {
        string singleObjectJson = """
        {
          "iceServers": {
            "urls": ["stun:stun.cloudflare.com:3478", "turn:turn.cloudflare.com:3478?transport=udp"],
            "username": "single-user",
            "credential": "single-pass"
          }
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(singleObjectJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal(2, servers[0].Urls.Count);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].Urls[0]);
        Assert.Equal("single-user", servers[0].Username);
        Assert.Equal("single-pass", servers[0].Credential);
    }

    [Fact]
    public async Task GetIceServersAsync_NormalizesArraySchema()
    {
        string arrayJson = """
        {
          "iceServers": [
            {
              "urls": ["stun:stun.cloudflare.com:3478"]
            },
            {
              "urls": ["turn:turn.cloudflare.com:3478?transport=udp"],
              "username": "array-user",
              "credential": "array-pass"
            }
          ]
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(arrayJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].Urls[0]);
        Assert.Null(servers[0].Username);
        Assert.Equal("turn:turn.cloudflare.com:3478?transport=udp", servers[1].Urls[0]);
        Assert.Equal("array-user", servers[1].Username);
        Assert.Equal("array-pass", servers[1].Credential);
    }

    [Fact]
    public async Task GetIceServersAsync_NormalizesSingleUrlStringField()
    {
        string stringUrlJson = """
        {
          "iceServers": [
            {
              "url": "stun:stun.cloudflare.com:3478"
            }
          ]
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(stringUrlJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].Urls[0]);
    }

    [Fact]
    public async Task GetIceServersAsync_NormalizesCommaSeparatedUrlsStringField()
    {
        string commaSeparatedJson = """
        {
          "iceServers": [
            {
              "urls": "stun:stun1.example.com:3478, stun:stun2.example.com:3478",
              "username": "csv-user",
              "credential": "csv-pass"
            }
          ]
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(commaSeparatedJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal(2, servers[0].Urls.Count);
        Assert.Equal("stun:stun1.example.com:3478", servers[0].Urls[0]);
        Assert.Equal("stun:stun2.example.com:3478", servers[0].Urls[1]);
        Assert.Equal("csv-user", servers[0].Username);
    }

    [Fact]
    public async Task GetIceServersAsync_FallsBackGracefully_OnHttpErrorStatuses()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"Internal Cloudflare Calls error\"}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true,
            FallbackIceServers = [new IceServer(["stun:custom.fallback:3478"])]
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal("stun:custom.fallback:3478", servers[0].Urls[0]);
    }

    [Fact]
    public async Task GetIceServersAsync_FallsBackToDefaultStun_WhenNoFallbackConfiguredAndApiFails()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Unauthorized);
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true,
            FallbackIceServers = []
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].Urls[0]);
    }

    [Fact]
    public async Task GetIceServersAsync_FallsBackGracefully_OnNetworkException()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            throw new HttpRequestException("Simulated connection timeout.");
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = true,
            FallbackIceServers = [new IceServer(["stun:custom.fallback:3478"])]
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Single(servers);
        Assert.Equal("stun:custom.fallback:3478", servers[0].Urls[0]);
    }

    [Fact]
    public async Task GetIceServersAsync_ReturnsFallback_WhenDisabled()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            throw new InvalidOperationException("Upstream HTTP call should never be made when disabled.");
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "key",
            ApiToken = "token",
            Enabled = false,
            FallbackIceServers = [new IceServer(["stun:custom.fallback:3478"])]
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Equal(0, mockHandler.CallCount);
        Assert.Single(servers);
        Assert.Equal("stun:custom.fallback:3478", servers[0].Urls[0]);
    }

    [Fact]
    public async Task GetIceServersAsync_ReturnsFallback_WhenCredentialsUnconfigured()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            throw new InvalidOperationException("Upstream HTTP call should never be made without credentials.");
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<CloudflareOptions> options = Options.Create(new CloudflareOptions
        {
            TurnKeyId = "",
            ApiToken = "",
            Enabled = true
        });

        CloudflareTurnProvider provider = new(httpClient, options, NullLogger<CloudflareTurnProvider>.Instance);

        IReadOnlyList<IceServer> servers = await provider.GetIceServersAsync();

        Assert.Equal(0, mockHandler.CallCount);
        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].Urls[0]);
    }
}

#endregion

#region Go2RtcBackendTests

public sealed class Go2RtcBackendTests
{
    [Fact]
    public async Task NegotiateOfferAsync_SendsSdpOfferAndParsesTextAnswer()
    {
        string expectedUrl = "http://localhost:1984/api/webrtc?src=cam1";
        string offerSdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test";
        string answerSdp = "v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=answer";

        MockHttpMessageHandler mockHandler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedUrl, request.RequestUri?.ToString());
            Assert.Equal("application/sdp", request.Content?.Headers.ContentType?.MediaType);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(answerSdp, Encoding.UTF8, "application/sdp")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        SdpAnswer answer = await backend.NegotiateOfferAsync("cam1", offerSdp);

        Assert.Equal(answerSdp, answer.Sdp);
        Assert.Equal("answer", answer.Type);
        Assert.Equal(offerSdp, mockHandler.LastRequestBody);
    }

    [Fact]
    public async Task NegotiateOfferAsync_ParsesJsonSdpAnswer()
    {
        string jsonAnswer = """
        {
          "type": "answer",
          "sdp": "v=0\r\no=- 3 3 IN IP4 127.0.0.1\r\ns=json_answer"
        }
        """;

        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonAnswer, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        SdpAnswer answer = await backend.NegotiateOfferAsync("cam1", "offer");

        Assert.Equal("v=0\r\no=- 3 3 IN IP4 127.0.0.1\r\ns=json_answer", answer.Sdp);
        Assert.Equal("answer", answer.Type);
    }

    [Fact]
    public async Task NegotiateOfferAsync_UsesDefaultStream_WhenStreamNameIsEmpty()
    {
        MockHttpMessageHandler mockHandler = new((request, _) =>
        {
            Assert.Equal("http://localhost:1984/api/webrtc?src=octocamrtc", request.RequestUri?.ToString());

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("v=0\r\nanswer", Encoding.UTF8, "application/sdp")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        SdpAnswer answer = await backend.NegotiateOfferAsync("", "offer");

        Assert.Equal("v=0\r\nanswer", answer.Sdp);
    }

    [Fact]
    public async Task NegotiateOfferAsync_ThrowsHttpRequestException_OnUpstreamHttpError()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal go2rtc error", Encoding.UTF8, "text/plain")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => backend.NegotiateOfferAsync("cam1", "offer"));
    }

    [Fact]
    public async Task CaptureSnapshotAsync_QueriesFrameJpegAndReturnsBytes()
    {
        byte[] expectedBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];

        MockHttpMessageHandler mockHandler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://localhost:1984/api/frame.jpeg?src=cam1", request.RequestUri?.ToString());

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        byte[] snapshotBytes = await backend.CaptureSnapshotAsync("cam1");

        Assert.Equal(expectedBytes, snapshotBytes);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_ThrowsHttpRequestException_OnHttpError()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Stream not found", Encoding.UTF8, "text/plain")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => backend.CaptureSnapshotAsync("unknown"));
    }

    [Fact]
    public async Task GetAvailableStreamsAsync_ParsesJsonKeysIntoSortedList()
    {
        string streamsJson = """
        {
          "zeta_cam": {},
          "alpha_cam": {},
          "beta_cam": {}
        }
        """;

        MockHttpMessageHandler mockHandler = new((request, _) =>
        {
            Assert.Equal("http://localhost:1984/api/streams", request.RequestUri?.ToString());

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(streamsJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        IReadOnlyList<string> streams = await backend.GetAvailableStreamsAsync();

        Assert.Equal(3, streams.Count);
        Assert.Equal("alpha_cam", streams[0]);
        Assert.Equal("beta_cam", streams[1]);
        Assert.Equal("zeta_cam", streams[2]);
    }

    [Fact]
    public async Task GetAvailableStreamsAsync_ReturnsEmptyList_OnHttpErrorOrException()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.InternalServerError);
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984",
            DefaultStream = "octocamrtc"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        IReadOnlyList<string> streams = await backend.GetAvailableStreamsAsync();

        Assert.Empty(streams);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsTrue_OnSuccess()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK);
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        bool reachable = await backend.CheckHealthAsync();

        Assert.True(reachable);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsFalse_OnFailure()
    {
        MockHttpMessageHandler mockHandler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.BadGateway);
            return Task.FromResult(response);
        });

        HttpClient httpClient = new(mockHandler);
        IOptions<Go2RtcOptions> options = Options.Create(new Go2RtcOptions
        {
            BaseUrl = "http://localhost:1984"
        });

        Go2RtcBackend backend = new(httpClient, options, NullLogger<Go2RtcBackend>.Instance);

        bool reachable = await backend.CheckHealthAsync();

        Assert.False(reachable);
    }
}

#endregion

#region GatewayApiIntegrationTests

public sealed class GatewayApiIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetHealth_Returns200AndHealthyStatus_WhenBackendReachable()
    {
        StubMediaBackend mediaBackend = new(isHealthy: true);
        CustomGatewayFactory factory = new(
            mediaBackend: mediaBackend,
            configureCloudflare: opt =>
            {
                opt.TurnKeyId = "test-key-id";
                opt.ApiToken = "test-api-token";
            });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadFromJsonAsync<GatewayHealth>(JsonOptions);
        Assert.NotNull(health);
        Assert.Equal("Healthy", health.Status);
        Assert.True(health.BackendReachable);
        Assert.True(health.CloudflareConfigured);
    }

    [Fact]
    public async Task GetHealth_Returns200AndDegradedStatus_WhenBackendUnreachable()
    {
        StubMediaBackend mediaBackend = new(isHealthy: false);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GatewayHealth? health = await response.Content.ReadFromJsonAsync<GatewayHealth>(JsonOptions);
        Assert.NotNull(health);
        Assert.Equal("Degraded", health.Status);
        Assert.False(health.BackendReachable);
    }

    [Fact]
    public async Task GetStreams_Returns200AndStreamsResponse()
    {
        StubMediaBackend mediaBackend = new(streams: ["cam_alpha", "cam_beta"]);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/streams");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        StreamsResponse? streamsResponse = await response.Content.ReadFromJsonAsync<StreamsResponse>(JsonOptions);
        Assert.NotNull(streamsResponse);
        Assert.Equal(2, streamsResponse.Streams.Count);
        Assert.Equal("cam_alpha", streamsResponse.Streams[0]);
        Assert.Equal("cam_beta", streamsResponse.Streams[1]);
        Assert.Equal("octocamrtc", streamsResponse.DefaultStream);
    }

    [Fact]
    public async Task GetIceServers_Returns200AndNeverLeaksMasterSecrets()
    {
        string secretKeyId = "SECRET_MASTER_KEY_ID_DO_NOT_LEAK";
        string secretToken = "SECRET_MASTER_API_TOKEN_DO_NOT_LEAK";

        StubIceServerProvider iceProvider = new([
            new IceServer(["stun:stun.cloudflare.com:3478"]),
            new IceServer(["turn:turn.cloudflare.com:3478?transport=udp"], "ephemeral_user", "ephemeral_pass")
        ]);

        CustomGatewayFactory factory = new(
            iceServerProvider: iceProvider,
            configureCloudflare: opt =>
            {
                opt.TurnKeyId = secretKeyId;
                opt.ApiToken = secretToken;
            });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/ice-servers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();

        // Verify master secrets are strictly server-side and never exposed to clients
        Assert.DoesNotContain(secretKeyId, responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretToken, responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TurnKeyId", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiToken", responseBody, StringComparison.OrdinalIgnoreCase);

        IceServerResponse? iceResponse = JsonSerializer.Deserialize<IceServerResponse>(responseBody, JsonOptions);
        Assert.NotNull(iceResponse);
        Assert.Equal(2, iceResponse.IceServers.Count);
        Assert.Equal("ephemeral_user", iceResponse.IceServers[1].Username);
    }

    [Fact]
    public async Task PostWebRtc_Returns200AndSdpAnswer_OnValidOffer()
    {
        StubMediaBackend mediaBackend = new(sdpAnswer: new SdpAnswer("v=0\r\no=negotiated_answer"));
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        WebRtcOfferRequest requestPayload = new("v=0\r\no=test_offer", "offer", "cam1");
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/webrtc", requestPayload, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SdpAnswer? answer = await response.Content.ReadFromJsonAsync<SdpAnswer>(JsonOptions);
        Assert.NotNull(answer);
        Assert.Equal("v=0\r\no=negotiated_answer", answer.Sdp);
        Assert.Equal("answer", answer.Type);
    }

    [Fact]
    public async Task PostWebRtc_Returns400BadRequest_WhenOfferSdpIsEmptyOrWhitespace()
    {
        CustomGatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        WebRtcOfferRequest emptyPayload = new("", "offer", "cam1");
        using HttpResponseMessage responseEmpty = await client.PostAsJsonAsync("/api/webrtc", emptyPayload, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, responseEmpty.StatusCode);

        WebRtcOfferRequest whitespacePayload = new("   ", "offer", "cam1");
        using HttpResponseMessage responseWhitespace = await client.PostAsJsonAsync("/api/webrtc", whitespacePayload, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, responseWhitespace.StatusCode);
    }

    [Fact]
    public async Task PostWebRtc_Returns502BadGateway_WhenBackendFails()
    {
        StubMediaBackend mediaBackend = new(shouldThrow: true);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        WebRtcOfferRequest requestPayload = new("v=0\r\no=test_offer", "offer", "cam1");
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/webrtc", requestPayload, JsonOptions);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_Returns200AndJpegImage_OnSuccess()
    {
        byte[] expectedJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        StubMediaBackend mediaBackend = new(snapshot: expectedJpeg);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/snapshot?stream=cam1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(expectedJpeg, responseBytes);
    }

    [Fact]
    public async Task GetSnapshot_SupportsSrcQueryParameter()
    {
        byte[] expectedJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        StubMediaBackend mediaBackend = new(snapshot: expectedJpeg);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/snapshot?src=cam2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetSnapshot_Returns503_WhenBackendFails()
    {
        StubMediaBackend mediaBackend = new(shouldThrow: true);
        CustomGatewayFactory factory = new(mediaBackend: mediaBackend);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/snapshot?stream=cam_error");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task CorsHeaders_ArePresent_OnApiEndpoints()
    {
        CustomGatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/streams");
        request.Headers.Add("Origin", "http://octopi.local");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        IEnumerable<string> corsValues = response.Headers.GetValues("Access-Control-Allow-Origin");
        Assert.Contains("*", corsValues);
    }
}

#endregion
