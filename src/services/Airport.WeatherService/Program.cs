using Airport.Contracts;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();

// Latest snapshot is kept in memory so GET /weather always has something to return,
// even before the first publish tick has run.
builder.Services.AddSingleton<WeatherCache>();
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

app.MapGet("/weather", (WeatherCache cache) => Results.Ok(cache.Current))
   .WithName("GetCurrentWeather");

app.Run();

// ---------------------------------------------------------------------------

internal sealed class WeatherCache
{
    public WeatherSnapshot Current { get; set; } = new(
        DateTimeOffset.UtcNow,
        "CAVOK",
        15,
        WindKnots: 5,
        WindDirectionDegrees: 270,
        VisibilityMeters: 10_000,
        CloudBaseFeet: 5_000);
}

internal sealed class WeatherPublisher(
    DaprClient dapr,
    WeatherCache cache,
    ILogger<WeatherPublisher> logger) : BackgroundService
{
    private static readonly string[] s_conditions =
    {
        "CAVOK", "Scattered clouds", "Broken clouds", "Overcast", "Light rain",
        "Heavy rain", "Thunderstorms", "Fog", "Snow", "Gusty winds"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup so the Dapr sidecar is definitely ready.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var rng = Random.Shared;
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(5));

        do
        {
            var snapshot = new WeatherSnapshot(
                ObservedAt: DateTimeOffset.UtcNow,
                Condition: s_conditions[rng.Next(s_conditions.Length)],
                TemperatureCelsius: Math.Round(rng.NextDouble() * 35 - 5, 1),
                WindKnots: rng.Next(0, 45),
                WindDirectionDegrees: rng.Next(0, 360),
                VisibilityMeters: rng.Next(500, 10_001),
                CloudBaseFeet: rng.Next(100, 8_001));

            cache.Current = snapshot;

            try
            {
                await dapr.PublishEventAsync(
                    DaprTopics.PubSubName,
                    DaprTopics.WeatherUpdates,
                    snapshot,
                    stoppingToken);

                logger.LogInformation(
                    "Published weather: {Condition} {Temp}°C wind {Wind}kt vis {Vis}m flyable={Flyable}",
                    snapshot.Condition, snapshot.TemperatureCelsius, snapshot.WindKnots,
                    snapshot.VisibilityMeters, snapshot.IsFlyable);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish weather snapshot (Dapr sidecar not ready yet?)");
            }
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken));
    }
}
