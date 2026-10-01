using System.Diagnostics;
using Airport.Contracts;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>
/// Tries once to acquire the Dapr distributed lock for the given gate. Returns true on success,
/// false if some other flight currently holds it. The workflow loops on this until it succeeds.
/// </summary>
/// <remarks>
/// Uses the alpha Dapr distributed-lock API. The lock owner is the flight id (== workflow id),
/// and the expiry acts as a safety net in case the workflow crashes without releasing.
/// </remarks>
public sealed class TryAcquireGateActivity(
    DaprClient dapr, FlightStateGate gate, ILogger<TryAcquireGateActivity> logger)
    : WorkflowActivity<GateLockInput, bool>
{
    // The distributed-lock API is still flagged Experimental in the Dapr .NET SDK.
#pragma warning disable DAPR_DISTRIBUTEDLOCK
    public override async Task<bool> RunAsync(WorkflowActivityContext context, GateLockInput input)
    {
        using var lease = await gate.EnterAsync();
        if (!await gate.ContainsAsync(input.FlightId))
            return false;
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.try_acquire_gate",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"try acquire gate {input.Gate}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.gate", input.Gate);
        }

        var resourceId = $"gate:{input.Gate}";
        // 5 minutes is generous enough for boarding + pushback + a buffer; the workflow
        // unlocks explicitly the moment it transitions to Departed.
        var response = await dapr.Lock(
            storeName: DaprTopics.LockStoreName,
            resourceId: resourceId,
            lockOwner: input.FlightId,
            expiryInSeconds: 300);

        span?.SetTag("gate.lock_acquired", response.Success);
        if (response.Success)
        {
            logger.LogInformation("Acquired gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        else
        {
            logger.LogDebug("Gate {Gate} busy; flight {FlightId} waiting", input.Gate, input.FlightId);
        }
        return response.Success;
    }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
}
