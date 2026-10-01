using Airport.Contracts;
using Airport.WeatherService;
using Airport.ServiceDefaults;
using Dapr;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();
builder.Services.AddSingleton<WeatherEventPublisher>();

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

app.MapGet("/weather/status", (WeatherState state) => Results.Ok(state.Status))
   .WithName("GetWeatherStatus");

// --- Control endpoints -----------------------------------------------------

app.MapPost("/weather/pause", async (WeatherState state, AirportUpdateNotifier updates) =>
{
    if (state.SetPaused(true))
        await updates.ChangedAsync();
    return Results.Ok();
});

app.MapPost("/weather/resume", async (
    WeatherState state, WeatherEventPublisher publisher, AirportUpdateNotifier updates,
    CancellationToken cancellationToken) =>
{
    if (state.SetPaused(false))
        await updates.ChangedAsync();
    // Publish the current value immediately so subscribers stop seeing stale data.
    await publisher.PublishAsync(state.Current, state.Override is not null, cancellationToken);
    return Results.Ok();
});

app.MapPut("/weather/override", async (
    WeatherSnapshot snapshot, WeatherState state, WeatherEventPublisher publisher,
    AirportUpdateNotifier updates, CancellationToken cancellationToken) =>
{
    var pinned = state.SetOverride(snapshot, presetName: null);
    await updates.ChangedAsync();
    await publisher.PublishAsync(pinned, true, cancellationToken);
    return Results.Ok(pinned);
});

app.MapDelete("/weather/override", async (WeatherState state, AirportUpdateNotifier updates) =>
{
    if (state.ClearOverride())
        await updates.ChangedAsync();
    // Don't publish here: let the next random tick reflect the change naturally.
    return Results.NoContent();
});

app.MapPost("/weather/preset/{name}", async (
    string name, WeatherState state, WeatherEventPublisher publisher,
    AirportUpdateNotifier updates, CancellationToken cancellationToken) =>
{
    var snapshot = WeatherPresets.TryGet(name);
    if (snapshot is null) return Results.NotFound($"Unknown preset '{name}'.");

    var pinned = state.SetOverride(snapshot, presetName: name.ToLowerInvariant());
    await updates.ChangedAsync();
    await publisher.PublishAsync(pinned, true, cancellationToken);
    return Results.Ok(pinned);
});

app.Run();

// ---------------------------------------------------------------------------

internal sealed class WeatherState
{
    private readonly Lock _gate = new();
    private WeatherSnapshot _current = WeatherPresets.Cavok();
    private bool _paused;

    public WeatherStatus Status
    {
        get
        {
            lock (_gate)
                return new(_current, _paused, Override is not null, PresetName,
                    (int)WeatherPublisher.PublishInterval.TotalSeconds);
        }
    }

    public WeatherSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>When true, the periodic publisher skips publishing so subscribers see stale data.</summary>
    public bool Paused
    {
        get { lock (_gate) return _paused; }
    }

    public bool SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused) return false;
            _paused = paused;
            return true;
        }
    }

    /// <summary>When non-null, the periodic publisher republishes this snapshot every tick.</summary>
    public WeatherSnapshot? Override { get; private set; }

    /// <summary>Optional friendly preset name behind the current override (for diagnostics).</summary>
    public string? PresetName { get; private set; }

    public WeatherSnapshot SetOverride(WeatherSnapshot snapshot, string? presetName)
    {
        lock (_gate)
        {
            _current = Stamp(snapshot);
            Override = _current;
            PresetName = presetName;
            return _current;
        }
    }

    public WeatherSnapshot? NextSnapshot(WeatherSnapshot generated)
    {
        lock (_gate)
        {
            if (Paused) return null;
            _current = Stamp(Override ?? generated);
            return _current;
        }
    }

    private WeatherSnapshot Stamp(WeatherSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        return snapshot with { ObservedAt = now > _current.ObservedAt ? now : _current.ObservedAt.AddTicks(1) };
    }

    public bool ClearOverride()
    {
        lock (_gate)
        {
            if (Override is null) return false;
            Override = null;
            PresetName = null;
            return true;
        }
    }
}

internal sealed class WeatherPublisher(
    WeatherEventPublisher publisher,
    WeatherState state,
    AirportUpdateNotifier updates,
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

            // Select overrides atomically so this tick cannot overwrite a newly applied preset.
            var snapshot = state.NextSnapshot(new WeatherSnapshot(
                ObservedAt: DateTimeOffset.UtcNow,
                Condition: s_conditions[rng.Next(s_conditions.Length)],
                TemperatureCelsius: Math.Round(rng.NextDouble() * 20 + 5, 1),
                WindKnots: rng.Next(0, 20),
                WindDirectionDegrees: rng.Next(0, 360),
                VisibilityMeters: rng.Next(4_000, 10_001),
                CloudBaseFeet: rng.Next(1_500, 8_001)));
            if (snapshot is null)
            {
                logger.LogDebug("Weather publishing was paused during this tick");
                continue;
            }

            try
            {
                await updates.ChangedAsync(stoppingToken);
                await publisher.PublishAsync(snapshot, state.Override is not null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (DaprException ex)
            {
                logger.LogWarning(ex, "Weather publication will retry on the next tick");
            }
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken));
    }
}
