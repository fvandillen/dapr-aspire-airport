using System.Diagnostics;
using Airport.Contracts;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Releases the gate lock held by the flight. Safe to call even if no lock is held.</summary>
public sealed class ReleaseGateActivity(DaprClient dapr, ILogger<ReleaseGateActivity> logger)
    : WorkflowActivity<GateLockInput, bool>
{
#pragma warning disable DAPR_DISTRIBUTEDLOCK
    public override async Task<bool> RunAsync(WorkflowActivityContext context, GateLockInput input)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.release_gate",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"release gate {input.Gate}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.gate", input.Gate);
        }

        var resourceId = $"gate:{input.Gate}";
        try
        {
            var response = await dapr.Unlock(
                storeName: DaprTopics.LockStoreName,
                resourceId: resourceId,
                lockOwner: input.FlightId);
            logger.LogInformation("Released gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            // Best-effort: the lock will expire on its own.
            logger.LogWarning(ex, "Failed to release gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        return true;
    }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
}
