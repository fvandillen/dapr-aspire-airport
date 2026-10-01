using System.Diagnostics;
using Airport.Contracts;
using Dapr;
using Dapr.Client;

namespace Airport.WeatherService;

public sealed class WeatherEventPublisher(DaprClient dapr, ILogger<WeatherEventPublisher> logger)
{
    public async Task PublishAsync(WeatherSnapshot snapshot, bool overrideActive, CancellationToken cancellationToken)
    {
        using var span = AirportTelemetry.Source.StartActivity("weather.publish", ActivityKind.Producer);
        span?.SetTag("weather.condition", snapshot.Condition);
        span?.SetTag("weather.flyable", snapshot.IsFlyable);
        span?.SetTag("weather.wind_kt", snapshot.WindKnots);
        span?.SetTag("weather.visibility_m", snapshot.VisibilityMeters);
        span?.SetTag("weather.override_active", overrideActive);

        try
        {
            await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.WeatherUpdates, snapshot, cancellationToken);
        }
        catch (DaprException ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Could not publish weather change {Condition}", snapshot.Condition);
            throw;
        }

        AirportTelemetry.WeatherPublished.Add(1,
            new KeyValuePair<string, object?>("weather.condition", snapshot.Condition),
            new KeyValuePair<string, object?>("weather.flyable", snapshot.IsFlyable));
        logger.LogInformation("Published weather: {Condition}, wind {Wind}kt, visibility {Visibility}m, flyable={Flyable}",
            snapshot.Condition, snapshot.WindKnots, snapshot.VisibilityMeters, snapshot.IsFlyable);
    }
}
