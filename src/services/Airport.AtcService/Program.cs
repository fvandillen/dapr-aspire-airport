using Airport.Contracts;
using System.Text.Json;
using Dapr;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();
builder.Services.AddSingleton<RunwayBoard>();
builder.Services.AddSingleton<WeatherWatch>();
builder.Services.AddSingleton<AtcStateGate>();

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
const string LastResetKey = "last-airport-reset";

// --- Pub/sub subscriber: weather updates -----------------------------------
app.MapPost("/atc/weather-updates",
    async (WeatherSnapshot snapshot, WeatherWatch watch, AtcStateGate gate, ILogger<Program> logger) =>
{
    using var lease = await gate.EnterAsync();
    if (!watch.Observe(snapshot))
    {
        logger.LogDebug("Ignoring duplicate or older weather snapshot from {ObservedAt}", snapshot.ObservedAt);
        return Results.Ok();
    }
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
    AtcStateGate gate,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    using var lease = await gate.EnterAsync(cancellationToken);
    var lastReset = await dapr.GetStateAsync<AirportResetRequest>(DaprTopics.StateStoreName, LastResetKey,
        cancellationToken: cancellationToken);
    if (lastReset is not null && request.RequestedAt <= lastReset.ResetAt)
    {
        logger.LogInformation("Dropping pre-reset clearance request for flight {FlightId}", request.FlightId);
        return Results.Ok();
    }
    using var span = AirportTelemetry.Source.StartActivity(
        "atc.decide_clearance",
        System.Diagnostics.ActivityKind.Consumer,
        parentContext: FlightTrace.ContextFor(request.FlightId));
    if (span is not null)
    {
        span.DisplayName = $"atc decide {request.Kind} for {request.Callsign}";
        span.SetTag("clearance.kind", request.Kind.ToString());
        span.SetTag("flight.id", request.FlightId);
        span.SetTag("flight.callsign", request.Callsign);
    }

    logger.LogInformation("{Callsign}: {Kind} clearance requested",
        request.Callsign, request.Kind);

    var sw = System.Diagnostics.Stopwatch.StartNew();
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

    sw.Stop();
    span?.SetTag("clearance.granted", result.Granted);
    span?.SetTag("clearance.runway", result.Runway);
    span?.SetTag("clearance.reason", result.Reason);

    AirportTelemetry.ClearanceDecisions.Add(1,
        new KeyValuePair<string, object?>("clearance.kind", request.Kind.ToString()),
        new KeyValuePair<string, object?>("clearance.granted", result.Granted),
        new KeyValuePair<string, object?>("runway", result.Runway));
    AirportTelemetry.ClearanceDecisionDurationMs.Record(sw.Elapsed.TotalMilliseconds,
        new KeyValuePair<string, object?>("clearance.kind", request.Kind.ToString()),
        new KeyValuePair<string, object?>("clearance.granted", result.Granted));

    logger.LogInformation("{Callsign}: {Decision} {Reason}",
        request.Callsign,
        result.Granted ? "GRANTED" : "DENIED",
        result.Reason);

    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.ClearanceRequests);

// --- Pub/sub subscriber: reset airport state and acknowledge completion ----
app.MapPost("/atc/airport-reset", async (
    AirportResetRequest request, DaprClient dapr, RunwayBoard runways,
    WeatherWatch watch, AtcStateGate gate, ILogger<Program> logger) =>
{
    using var lease = await gate.EnterAsync();
    var lastReset = await dapr.GetStateAsync<AirportResetRequest>(DaprTopics.StateStoreName, LastResetKey);
    if (lastReset is null || request.ResetAt > lastReset.ResetAt)
    {
        // Commit the replay cutoff and clearance deletion together; redelivery must not clear new flights.
        await dapr.ExecuteStateTransactionAsync(DaprTopics.StateStoreName,
        [
            new StateTransactionRequest(LastResetKey, JsonSerializer.SerializeToUtf8Bytes(request), StateOperationType.Upsert),
            new StateTransactionRequest(ActiveClearancesKey, Array.Empty<byte>(), StateOperationType.Delete),
        ]);
        runways.Clear();
        watch.Clear();
        logger.LogInformation("Cleared tower state for airport reset {WorkflowId}", request.WorkflowId);
    }
    await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.AirportResetCompleted,
        new AirportResetCompleted(request.WorkflowId));
    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.AirportResetRequests);

// --- HTTP API (read-only frontend views) -----------------------------------
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

    public void Clear()
    {
        lock (_gate) _busyUntil.Clear();
    }

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
    private readonly Lock _gate = new();
    private WeatherSnapshot? _latest;

    public void Clear()
    {
        lock (_gate) _latest = null;
    }

    public WeatherSnapshot? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public bool Observe(WeatherSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_latest is not null && _latest.ObservedAt >= snapshot.ObservedAt)
                return false;
            _latest = snapshot;
            return true;
        }
    }
}

public sealed class AtcStateGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
