using System.Diagnostics;
using Airport.Contracts;
using Airport.FlightOperations.Workflows;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Weather;

public sealed class WeatherUpdatesHandler(
    WeatherSnapshotStore snapshots,
    DaprClient dapr,
    DaprWorkflowClient workflows,
    ILogger<WeatherUpdatesHandler> logger)
{
    public async Task HandleAsync(WeatherSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var span = AirportTelemetry.Source.StartActivity("flight-ops.weather_updated", ActivityKind.Consumer);
        span?.SetTag("weather.condition", snapshot.Condition);
        span?.SetTag("weather.observed_at", snapshot.ObservedAt);
        if (!await snapshots.ObserveAsync(snapshot, cancellationToken))
            return;

        var ids = await dapr.GetStateAsync<List<string>>(DaprTopics.StateStoreName, FlightOps.IndexKey,
            cancellationToken: cancellationToken) ?? [];

        // Repeat notifications on redelivery: a previous attempt may have saved the
        // snapshot but failed partway through notifying the waiting workflows.
        foreach (var flightId in ids)
        {
            var state = await workflows.GetWorkflowStateAsync(flightId,
                getInputsAndOutputs: true, cancellation: cancellationToken);
            if (state is null)
            {
                logger.LogWarning("Could not inspect workflow {FlightId} for a weather notification", flightId);
                continue;
            }

            if (state.RuntimeStatus != WorkflowRuntimeStatus.Running ||
                state.ReadCustomStatusAs<string>() != FlightWorkflow.WaitingForWeatherStatus)
                continue;

            await workflows.RaiseEventAsync(flightId, FlightWorkflow.WeatherUpdatedEventName,
                true, cancellationToken);
            logger.LogInformation("Notified flight {FlightId} that weather was published", flightId);
        }
    }
}
