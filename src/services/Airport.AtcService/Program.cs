using Airport.Contracts;
using Dapr;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();
builder.Services.AddSingleton<RunwayBoard>();
builder.Services.AddSingleton<WeatherWatch>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Required for Dapr pub/sub: unwrap CloudEvents and publish the subscription manifest.
app.UseCloudEvents();
app.MapSubscribeHandler();

const string ActiveClearancesKey = "active-clearances";

// --- Pub/sub subscriber: weather updates -----------------------------------
app.MapPost("/atc/weather-updates",
    (WeatherSnapshot snapshot, WeatherWatch watch, ILogger<Program> logger) =>
{
    watch.Latest = snapshot;
    logger.LogDebug("Weather observed: {Condition} flyable={Flyable}",
        snapshot.Condition, snapshot.IsFlyable);
    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.WeatherUpdates);

// --- Pub/sub subscriber: clearance requests -> decision --------------------
app.MapPost("/atc/clearance-requests", async (
    ClearanceRequest request,
    DaprClient dapr,
    RunwayBoard runways,
    WeatherWatch watch,
    ILogger<Program> logger) =>
{
    logger.LogInformation("{Callsign}: {Kind} clearance requested",
        request.Callsign, request.Kind);

    var weather = watch.Latest;
    ClearanceResult result;

    if (weather is not null && !weather.IsFlyable)
    {
        result = new ClearanceResult(
            request.FlightId, request.Callsign, request.Kind,
            Granted: false, Runway: "-",
            Reason: $"Below minima: {weather.Condition}, wind {weather.WindKnots}kt, vis {weather.VisibilityMeters}m",
            DecidedAt: DateTimeOffset.UtcNow);
    }
    else
    {
        var runway = runways.TryReserveRunway(TimeSpan.FromSeconds(15));
        if (runway is null)
        {
            result = new ClearanceResult(
                request.FlightId, request.Callsign, request.Kind,
                Granted: false, Runway: "-",
                Reason: "All runways occupied — hold short",
                DecidedAt: DateTimeOffset.UtcNow);
        }
        else
        {
            result = new ClearanceResult(
                request.FlightId, request.Callsign, request.Kind,
                Granted: true, Runway: runway,
                Reason: $"Cleared {request.Kind} runway {runway}",
                DecidedAt: DateTimeOffset.UtcNow);

            var map = await dapr.GetStateAsync<Dictionary<string, ActiveClearance>>(
                DaprTopics.StateStoreName, ActiveClearancesKey)
                ?? new Dictionary<string, ActiveClearance>();

            map[request.FlightId] = new ActiveClearance(
                request.FlightId, request.Callsign, request.Kind, runway,
                GrantedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(15));

            await dapr.SaveStateAsync(DaprTopics.StateStoreName, ActiveClearancesKey, map);
        }
    }

    await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.ClearanceResults, result);

    logger.LogInformation("{Callsign}: {Decision} {Reason}",
        request.Callsign,
        result.Granted ? "GRANTED" : "DENIED",
        result.Reason);

    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.ClearanceRequests);

// --- HTTP API (read-only views for the frontend) ---------------------------
app.MapGet("/clearances", async (DaprClient dapr) =>
{
    var all = await dapr.GetStateAsync<Dictionary<string, ActiveClearance>>(
        DaprTopics.StateStoreName, ActiveClearancesKey)
        ?? new Dictionary<string, ActiveClearance>();

    var now = DateTimeOffset.UtcNow;
    var live = all.Values
        .Where(c => c.ExpiresAt > now)
        .OrderBy(c => c.GrantedAt)
        .ToArray();

    return Results.Ok(live);
});

app.MapGet("/atc/weather", (WeatherWatch watch) => Results.Ok(watch.Latest));

app.Run();

// ---------------------------------------------------------------------------

/// <summary>Tracks runway availability so two flights aren't cleared for the same runway at once.</summary>
public sealed class RunwayBoard
{
    private static readonly string[] s_runways = { "09L", "09R", "27L", "27R" };
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _busyUntil = new();

    public string? TryReserveRunway(TimeSpan duration)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var runway in s_runways)
            {
                if (!_busyUntil.TryGetValue(runway, out var until) || until <= now)
                {
                    _busyUntil[runway] = now + duration;
                    return runway;
                }
            }
            return null;
        }
    }
}

/// <summary>Singleton holding the last weather snapshot ATC saw via pub/sub.</summary>
public sealed class WeatherWatch
{
    public WeatherSnapshot? Latest { get; set; }
}
