using Airport.Contracts;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();

// Latest snapshot + control flags live in a singleton so HTTP handlers and the
// background publisher can read/write the same state.
builder.Services.AddSingleton<WeatherState>();
builder.Services.AddHostedService<WeatherPublisher>();

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

// --- Read endpoints --------------------------------------------------------

app.MapGet("/weather", (WeatherState state) => Results.Ok(state.Current))
   .WithName("GetCurrentWeather");

app.MapGet("/weather/status", (WeatherState state) => Results.Ok(new WeatherStatus(
    Current: state.Current,
    Paused: state.Paused,
    OverrideActive: state.Override is not null,
    PresetName: state.PresetName,
    PublishIntervalSeconds: (int)WeatherPublisher.PublishInterval.TotalSeconds)))
   .WithName("GetWeatherStatus");

// --- Control endpoints -----------------------------------------------------

app.MapPost("/weather/pause", async (WeatherState state) =>
{
    state.Paused = true;
    return Results.Ok();
});

app.MapPost("/weather/resume", async (WeatherState state, DaprClient dapr) =>
{
    state.Paused = false;
    // Publish the current value immediately so subscribers stop seeing stale data.
    await PublishCurrent(dapr, state);
    return Results.Ok();
});

app.MapPut("/weather/override", async (
    WeatherSnapshot snapshot, WeatherState state, DaprClient dapr) =>
{
    var pinned = snapshot with { ObservedAt = DateTimeOffset.UtcNow };
    state.SetOverride(pinned, presetName: null);
    await PublishCurrent(dapr, state);
    return Results.Ok(pinned);
});

app.MapDelete("/weather/override", (WeatherState state) =>
{
    state.ClearOverride();
    // Don't publish here: let the next random tick reflect the change naturally.
    return Results.NoContent();
});

app.MapPost("/weather/preset/{name}", async (
    string name, WeatherState state, DaprClient dapr) =>
{
    var snapshot = WeatherPresets.TryGet(name);
    if (snapshot is null) return Results.NotFound($"Unknown preset '{name}'.");

    state.SetOverride(snapshot, presetName: name.ToLowerInvariant());
    await PublishCurrent(dapr, state);
    return Results.Ok(snapshot);
});

app.Run();

// ---------------------------------------------------------------------------

static async Task PublishCurrent(DaprClient dapr, WeatherState state)
{
    try
    {
        await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.WeatherUpdates, state.Current);
    }
    catch
    {
        // Best-effort: the periodic loop will retry.
    }
}

// ---------------------------------------------------------------------------

internal sealed class WeatherState
{
    private readonly Lock _gate = new();
    private WeatherSnapshot _current = WeatherPresets.Cavok();

    public WeatherSnapshot Current
    {
        get { lock (_gate) return _current; }
        set { lock (_gate) _current = value; }
    }

    /// <summary>When true, the periodic publisher skips publishing so subscribers see stale data.</summary>
    public bool Paused { get; set; }

    /// <summary>When non-null, the periodic publisher republishes this snapshot every tick.</summary>
    public WeatherSnapshot? Override { get; private set; }

    /// <summary>Optional friendly preset name behind the current override (for diagnostics).</summary>
    public string? PresetName { get; private set; }

    public void SetOverride(WeatherSnapshot snapshot, string? presetName)
    {
        lock (_gate)
        {
            Override = snapshot;
            PresetName = presetName;
            _current = snapshot;
        }
    }

    public void ClearOverride()
    {
        lock (_gate)
        {
            Override = null;
            PresetName = null;
        }
    }
}

internal sealed class WeatherPublisher(
    DaprClient dapr,
    WeatherState state,
    ILogger<WeatherPublisher> logger) : BackgroundService
{
    /// <summary>Cadence between automatic weather snapshots. Slow enough that the demo audience can keep up.</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(60);

    private static readonly string[] s_conditions =
    {
        "CAVOK", "Scattered clouds", "Broken clouds", "Overcast", "Light rain"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup so the Dapr sidecar is definitely ready.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var rng = Random.Shared;
        using var ticker = new PeriodicTimer(PublishInterval);

        do
        {
            if (state.Paused)
            {
                logger.LogDebug("Weather publishing paused; skipping tick.");
                continue;
            }

            WeatherSnapshot snapshot;
            if (state.Override is { } pinned)
            {
                // Keep the same condition, but refresh the timestamp so subscribers see "live" data.
                snapshot = pinned with { ObservedAt = DateTimeOffset.UtcNow };
            }
            else
            {
                // Gentle random walk - mostly flyable, occasional weather but no wild swings.
                snapshot = new WeatherSnapshot(
                    ObservedAt: DateTimeOffset.UtcNow,
                    Condition: s_conditions[rng.Next(s_conditions.Length)],
                    TemperatureCelsius: Math.Round(rng.NextDouble() * 20 + 5, 1),
                    WindKnots: rng.Next(0, 20),
                    WindDirectionDegrees: rng.Next(0, 360),
                    VisibilityMeters: rng.Next(4_000, 10_001),
                    CloudBaseFeet: rng.Next(1_500, 8_001));
            }

            state.Current = snapshot;

            using var span = AirportTelemetry.Source.StartActivity("weather.publish");
            span?.SetTag("weather.condition", snapshot.Condition);
            span?.SetTag("weather.flyable", snapshot.IsFlyable);
            span?.SetTag("weather.wind_kt", snapshot.WindKnots);
            span?.SetTag("weather.visibility_m", snapshot.VisibilityMeters);
            span?.SetTag("weather.override_active", state.Override is not null);

            try
            {
                await dapr.PublishEventAsync(
                    DaprTopics.PubSubName, DaprTopics.WeatherUpdates, snapshot, stoppingToken);

                AirportTelemetry.WeatherPublished.Add(1,
                    new KeyValuePair<string, object?>("weather.condition", snapshot.Condition),
                    new KeyValuePair<string, object?>("weather.flyable", snapshot.IsFlyable));

                logger.LogInformation(
                    "Published weather: {Condition} {Temp}°C wind {Wind}kt vis {Vis}m flyable={Flyable} (override={Override})",
                    snapshot.Condition, snapshot.TemperatureCelsius, snapshot.WindKnots,
                    snapshot.VisibilityMeters, snapshot.IsFlyable, state.Override is not null);
            }
            catch (Exception ex)
            {
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                logger.LogWarning(ex, "Failed to publish weather snapshot (Dapr sidecar not ready yet?)");
            }
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken));
    }
}
