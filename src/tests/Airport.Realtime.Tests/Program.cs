using System.Collections.Concurrent;
using System.Diagnostics;
using Airport.Contracts;
using Airport.ServiceDefaults;
using Airport.Web.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;

await using var flights = await TestBackend.StartAsync();
await using var weather = await TestBackend.StartAsync();
await using var tower = await TestBackend.StartAsync();
await using var live = new AirportLiveState(
    new FlightsApi(new HttpClient { BaseAddress = flights.Uri }),
    new WeatherApi(new HttpClient { BaseAddress = weather.Uri }),
    new AtcApi(new HttpClient { BaseAddress = tower.Uri }),
    NullLogger<AirportLiveState>.Instance);

live.Start();
await Until(() => live.FlightsLive && live.WeatherLive && live.TowerLive, "initial snapshots");
Check(flights.Reads == 1 && weather.Reads == 1 && tower.Reads == 2, "one initial snapshot per endpoint");
var baseline = TotalReads();
await Task.Delay(2500);
Check(TotalReads() == baseline, "zero idle HTTP reads across more than one former polling interval");

var flight = new FlightView("FL-TEST", "TEST01", "AMS", "LHR", "Airbus A320",
    DateTimeOffset.UtcNow, null, null, FlightStatus.BoardingPushback, "B28", null, null);
flights.Flights = [flight];
await flights.NotifyAsync();
await Until(() => live.Flights.Count == 1, "flight notification");
Check(weather.Reads == 1 && tower.Reads == 2, "flight changes do not read weather or tower");

var blocked = flights.BlockNextRead();
flights.Flights = [flight with { Status = FlightStatus.Departed }];
await flights.NotifyAsync();
await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
flights.Flights = [flight with { Status = FlightStatus.Cruising }];
await flights.NotifyAsync();
await Task.Delay(150);
blocked.Release.TrySetResult();
await Until(() => live.Flights.Single().Status == FlightStatus.Cruising, "change received during a snapshot read");
Check(flights.MaxConcurrentReads == 1, "snapshot reads never overlap for a service");

var retainedWeather = live.Weather;
weather.FailReads = true;
await weather.NotifyAsync();
await Until(() => live.WeatherError is not null, "snapshot read failure");
Check(live.Weather == retainedWeather && !live.WeatherLive, "failed reads retain data and mark it stale");
weather.FailReads = false;
weather.Weather = weather.Weather with { Paused = true, PresetName = "cavok", OverrideActive = true };
await weather.NotifyAsync();
await Until(() => live.WeatherLive && live.Weather?.Paused == true, "weather control notification after failure");
Check(tower.Reads == 2, "weather publisher control changes do not read tower");

var now = DateTimeOffset.UtcNow;
tower.Clearances =
[
    new("FL-TEST", "TEST01", ClearanceKind.Takeoff, "09L", now, now.AddMilliseconds(600)),
    new("FL-OTHER", "TEST02", ClearanceKind.Landing, "09R", now, now.AddMilliseconds(1100)),
];
await tower.NotifyAsync();
await Until(() => live.ActiveClearances.Count() == 2, "clearance notification");
var beforeExpiry = TotalReads();
var revision = live.Revision;
await Until(() => live.Clearances.Count == 1 && live.Revision > revision, "first local clearance expiry");
await Until(() => live.Clearances.Count == 0, "last local clearance expiry");
Check(TotalReads() == beforeExpiry, "clearance expiry updates the UI without HTTP reads");

baseline = TotalReads();
await live.RefreshWeatherAsync();
Check(TotalReads() == baseline + 1, "manual refresh reads only the selected feed");

var oldUri = flights.Uri;
await flights.StopAsync();
await Until(() => live.FlightsError is not null, "disconnect is surfaced", 12000);
Check(live.Flights.Single().Status == FlightStatus.Cruising && !live.FlightsLive,
    "disconnection retains the last snapshot but never claims it is live");
await using var restarted = await TestBackend.StartAsync(oldUri);
restarted.Flights = [flight with { Status = FlightStatus.Landed }];
await Until(() => live.FlightsLive && live.Flights.Single().Status == FlightStatus.Landed,
    "reconnect re-synchronizes missed changes", 15000);
Check(restarted.Reads == 1, "reconnect reads one fresh snapshot");
baseline = TotalReads() + restarted.Reads;
await Task.Delay(2500);
Check(TotalReads() + restarted.Reads == baseline, "reconnected client remains idle without polling");

restarted.Flights = [];
await restarted.NotifyAsync();
await Until(() => live.Flights.Count == 0, "reset removes aircraft from the shared snapshot");
tower.TowerWeather = null;
tower.Clearances = [];
await tower.NotifyAsync();
await Until(() => live.TowerWeather is null && live.Clearances.Count == 0, "tower reset notification");

await using var lateServer = await TestBackend.StartAsync();
var lateUri = lateServer.Uri;
await lateServer.StopAsync();
var connectionError = "";
var lateReads = 0;
await using var lateFeed = new AirportLiveFeed(lateUri, "Late service", _ =>
{
    Interlocked.Increment(ref lateReads);
    return Task.CompletedTask;
}, error => connectionError = error ?? "", NullLogger.Instance);
lateFeed.Start();
await Until(() => connectionError.Contains("disconnected"), "initial connection failure is surfaced");
await using var available = await TestBackend.StartAsync(lateUri);
await Until(() => lateReads == 1 && connectionError.Length == 0, "initial connection retry", 12000);

var spans = flights.Exporter.Spans.Concat(weather.Exporter.Spans)
    .Concat(tower.Exporter.Spans).Concat(restarted.Exporter.Spans).ToArray();
Check(spans.Any(span => span.GetTagItem("url.path")?.ToString() == "/flights"),
    "normal airport requests remain traced");
Check(!spans.Any(span => span.Kind == ActivityKind.Server &&
    span.GetTagItem("url.path")?.ToString()?.StartsWith(AirportUpdates.HubPath) == true),
    "SignalR transport connections are not exported as airport traces");

Console.WriteLine("PASS: idle reads, scoped notifications, in-flight changes, stale data, local expiry, manual refresh, reconnect, reset, startup retry and trace filtering.");

int TotalReads() => flights.Reads + weather.Reads + tower.Reads;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
}

static async Task Until(Func<bool> predicate, string description, int timeoutMs = 5000)
{
    using var timeout = new CancellationTokenSource(timeoutMs);
    while (!predicate())
    {
        if (timeout.IsCancellationRequested)
            throw new InvalidOperationException($"Timed out waiting for {description}");
        await Task.Delay(20);
    }
}

sealed class TestBackend(WebApplication app, TraceExporter exporter) : IAsyncDisposable
{
    private int _reads;
    private int _concurrentReads;
    private int _maxConcurrentReads;
    private BlockedRead? _blocked;
    private bool _stopped;

    public Uri Uri { get; private set; } = null!;
    public TraceExporter Exporter => exporter;
    public int Reads => _reads;
    public int MaxConcurrentReads => _maxConcurrentReads;
    public bool FailReads { get; set; }
    public FlightView[] Flights { get; set; } = [];
    public ActiveClearance[] Clearances { get; set; } = [];
    public WeatherStatus Weather { get; set; } = new(WeatherPresets.Cavok(), false, false, null, 60);
    public WeatherSnapshot? TowerWeather { get; set; } = WeatherPresets.Cavok();

    public static async Task<TestBackend> StartAsync(Uri? uri = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
        builder.WebHost.UseUrls(uri?.ToString().TrimEnd('/') ?? "http://127.0.0.1:0");
        builder.AddServiceDefaults();
        var exporter = new TraceExporter();
        builder.Services.AddOpenTelemetry().WithTracing(trace =>
            trace.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        var app = builder.Build();
        var backend = new TestBackend(app, exporter);
        app.MapDefaultEndpoints();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs"))
            {
                await next(context);
                return;
            }
            Interlocked.Increment(ref backend._reads);
            var concurrent = Interlocked.Increment(ref backend._concurrentReads);
            backend._maxConcurrentReads = Math.Max(backend._maxConcurrentReads, concurrent);
            try
            {
                if (backend.FailReads)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                await next(context);
            }
            finally
            {
                Interlocked.Decrement(ref backend._concurrentReads);
            }
        });
        app.MapGet("/flights", async () =>
        {
            var snapshot = backend.Flights;
            var blocked = Interlocked.Exchange(ref backend._blocked, null);
            if (blocked is not null)
            {
                blocked.Started.TrySetResult();
                await blocked.Release.Task;
            }
            return Results.Ok(snapshot);
        });
        app.MapGet("/weather/status", () => Results.Ok(backend.Weather));
        app.MapGet("/clearances", () => Results.Ok(backend.Clearances));
        app.MapGet("/atc/weather", () =>
            backend.TowerWeather is { } snapshot ? Results.Ok(snapshot) : Results.NoContent());
        await app.StartAsync();
        backend.Uri = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return backend;
    }

    public BlockedRead BlockNextRead() => _blocked = new BlockedRead();

    public Task NotifyAsync() => app.Services.GetRequiredService<AirportUpdateNotifier>().ChangedAsync();

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await app.StopAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await app.DisposeAsync();
    }
}

sealed class BlockedRead
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

sealed class TraceExporter : BaseExporter<Activity>
{
    public ConcurrentBag<Activity> Spans { get; } = [];

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch) Spans.Add(activity);
        return ExportResult.Success;
    }
}
