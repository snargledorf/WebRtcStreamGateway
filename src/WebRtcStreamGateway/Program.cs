using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRtcStreamGateway.Domain;
using WebRtcStreamGateway.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CloudflareOptions>(builder.Configuration.GetSection(CloudflareOptions.SectionName));
builder.Services.Configure<Go2RtcOptions>(builder.Configuration.GetSection(Go2RtcOptions.SectionName));
builder.Services.Configure<SnapshotCacheOptions>(builder.Configuration.GetSection(SnapshotCacheOptions.SectionName));

builder.Services.AddHttpClient<IIceServerProvider, CloudflareTurnProvider>();
builder.Services.AddHttpClient<IMediaBackend, Go2RtcBackend>();
builder.Services.AddSingleton<ISnapshotCache, SnapshotCache>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GatewaySecurity");
        logger.LogError(ex, "Unhandled exception during request processing; redacting error details from client response");

        if (context.Response.HasStarted)
            throw;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync("{\"error\":\"An internal server error occurred.\"}");
    }
});

app.UseCors();

app.MapPost("/api/webrtc", async (
    [FromBody] WebRtcOfferRequest? request,
    IMediaBackend mediaBackend,
    IOptions<Go2RtcOptions> options,
    CancellationToken cancellationToken) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.Sdp))
        return Results.BadRequest(new { error = "SDP offer is required and cannot be empty." });

    string streamName = !string.IsNullOrWhiteSpace(request.Stream)
        ? request.Stream.Trim()
        : options.Value.DefaultStream;

    try
    {
        SdpAnswer answer = await mediaBackend.NegotiateOfferAsync(streamName, request.Sdp, cancellationToken);
        return Results.Ok(answer);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/snapshot", async (
    [FromQuery] string? stream,
    [FromQuery] string? src,
    IMediaBackend mediaBackend,
    ISnapshotCache snapshotCache,
    IOptions<Go2RtcOptions> options,
    CancellationToken cancellationToken) =>
{
    string targetStream = !string.IsNullOrWhiteSpace(stream)
        ? stream.Trim()
        : (!string.IsNullOrWhiteSpace(src) ? src.Trim() : options.Value.DefaultStream);

    if (snapshotCache.TryGet(targetStream, out byte[]? cachedBytes) && cachedBytes?.Length > 0)
        return Results.File(cachedBytes, "image/jpeg");

    try
    {
        byte[] freshBytes = await mediaBackend.CaptureSnapshotAsync(targetStream, cancellationToken);
        if (freshBytes.Length == 0)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        snapshotCache.Set(targetStream, freshBytes);
        return Results.File(freshBytes, "image/jpeg");
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/ice-servers", async (
    IIceServerProvider iceServerProvider,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<IceServer> iceServers = await iceServerProvider.GetIceServersAsync(cancellationToken);
    return Results.Ok(new IceServerResponse(iceServers));
});

app.MapGet("/api/streams", async (
    IMediaBackend mediaBackend,
    IOptions<Go2RtcOptions> options,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<string> streams = await mediaBackend.GetAvailableStreamsAsync(cancellationToken);
    return Results.Ok(new StreamsResponse(streams, options.Value.DefaultStream));
});

app.MapGet("/health", async (
    IMediaBackend mediaBackend,
    IOptions<CloudflareOptions> cloudflareOptions,
    CancellationToken cancellationToken) =>
{
    bool reachable = await mediaBackend.CheckHealthAsync(cancellationToken);
    bool cloudflareConfigured = !string.IsNullOrWhiteSpace(cloudflareOptions.Value.TurnKeyId) &&
                                !string.IsNullOrWhiteSpace(cloudflareOptions.Value.ApiToken);

    string status = reachable ? "Healthy" : "Degraded";
    var health = new GatewayHealth(status, reachable, cloudflareConfigured, DateTimeOffset.UtcNow);

    return Results.Ok(health);
});

app.Run();