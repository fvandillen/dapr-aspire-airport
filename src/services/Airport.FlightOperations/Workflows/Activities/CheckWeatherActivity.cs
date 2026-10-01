using System.Diagnostics;
using Airport.Contracts;
using Airport.FlightOperations.Weather;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Reads the latest weather received through pub/sub, or null before the first event.</summary>
public sealed class CheckWeatherActivity(WeatherSnapshotStore snapshots, ILogger<CheckWeatherActivity> logger)
    : WorkflowActivity<CheckWeatherInput, WeatherSnapshot?>
{
    public override async Task<WeatherSnapshot?> RunAsync(WorkflowActivityContext context, CheckWeatherInput _)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.check_weather",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(context.InstanceId));
        if (span is not null)
        {
            span.DisplayName = $"check weather for {context.InstanceId}";
            span.SetTag("flight.id", context.InstanceId);
        }

        var snapshot = await snapshots.GetLatestAsync();
        span?.SetTag("weather.available", snapshot is not null);
        if (snapshot is null)
        {
            logger.LogInformation("Flight {FlightId} is waiting for the first weather event", context.InstanceId);
            return null;
        }

        span?.SetTag("weather.condition", snapshot.Condition);
        span?.SetTag("weather.flyable", snapshot.IsFlyable);
        span?.SetTag("weather.wind_kt", snapshot.WindKnots);

        logger.LogInformation("Published weather check: {Condition}, flyable={Flyable}",
            snapshot.Condition, snapshot.IsFlyable);
        return snapshot;
    }
}
