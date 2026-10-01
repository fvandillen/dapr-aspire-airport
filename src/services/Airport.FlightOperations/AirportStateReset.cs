using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Airport.FlightOperations.Weather;
using Airport.FlightOperations.Workflows;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations;

public sealed class AirportStateReset(
    DaprClient dapr,
    DaprWorkflowClient workflows,
    ILogger<AirportStateReset> logger) : IDisposable
{
    private readonly SemaphoreSlim _acknowledgements = new(1, 1);
    private string? _activeResetId;

    // The /flights endpoint filter holds the same gate used by mutating activities.
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ClearCoreAsync(cancellationToken);
        }
        finally
        {
            await SetActiveResetAsync(null, CancellationToken.None);
        }
    }

    public async Task AcknowledgeAsync(AirportResetCompleted completed, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await _acknowledgements.WaitAsync(timeout.Token);
        try
        {
            if (_activeResetId != completed.WorkflowId)
            {
                logger.LogInformation("Ignoring acknowledgement for inactive reset {WorkflowId}", completed.WorkflowId);
                return;
            }
            await workflows.RaiseEventAsync(completed.WorkflowId, AirportResetWorkflow.CompletedEventName,
                completed, timeout.Token);
            logger.LogInformation("Routed ATC reset acknowledgement to workflow {WorkflowId}", completed.WorkflowId);
        }
        finally
        {
            _acknowledgements.Release();
        }
    }

    private async Task ClearCoreAsync(CancellationToken cancellationToken)
    {
        var ids = await dapr.GetStateAsync<List<string>>(DaprTopics.StateStoreName, FlightOps.IndexKey,
            cancellationToken: cancellationToken) ?? [];
        var workflowIds = new HashSet<string>();
        string? continuation = null;
        do
        {
            var page = await workflows.ListInstanceIdsAsync(continuation, pageSize: 100,
                cancellation: cancellationToken);
            workflowIds.UnionWith(page.InstanceIds);
            continuation = page.ContinuationToken;
        } while (continuation is not null);

        // A previous reset may have timed out while waiting for ATC.
        foreach (var previousResetId in workflowIds.Where(IsResetWorkflow).ToArray())
        {
            await StopAndPurgeAsync(previousResetId, cancellationToken);
            workflowIds.Remove(previousResetId);
        }

        // Include flights whose scheduling succeeded but whose index write failed.
        ids = ids.Where(id => !IsResetWorkflow(id)).Union(workflowIds).ToList();
        await dapr.SaveStateAsync(DaprTopics.StateStoreName, FlightOps.IndexKey, ids,
            cancellationToken: cancellationToken);

        foreach (var flightId in ids.ToArray())
        {
            string? workflowGate = null;
            if (workflowIds.Contains(flightId))
            {
                var state = await StopAndPurgeAsync(flightId, cancellationToken);
                workflowGate = state.ReadInputAs<FlightWorkflowInput>()?.Gate;
            }

            var actor = AircraftActorProxy.For(flightId);
            var aircraft = await actor.GetStateAsync();
            var assignedGate = string.IsNullOrEmpty(aircraft.Gate) ? workflowGate : aircraft.Gate;
            if (!string.IsNullOrEmpty(assignedGate))
            {
#pragma warning disable DAPR_DISTRIBUTEDLOCK
                var unlocked = await dapr.Unlock(DaprTopics.LockStoreName, $"gate:{assignedGate}", flightId,
                    cancellationToken: cancellationToken);
                // A missing lock or a lock owned by another flight must be left alone.
                if (unlocked.status == LockStatus.InternalError)
                    throw new InvalidOperationException($"Could not release gate {assignedGate} for {flightId}.");
#pragma warning restore DAPR_DISTRIBUTEDLOCK
            }
            await actor.ClearStateAsync();

            // Persist progress so a failed reset can be retried without resurrecting cleared flights.
            ids.Remove(flightId);
            await dapr.SaveStateAsync(DaprTopics.StateStoreName, FlightOps.IndexKey, ids,
                cancellationToken: cancellationToken);
            logger.LogInformation("Removed flight {FlightId}, aircraft state and workflow history", flightId);
        }

        var resetId = $"{AirportResetWorkflow.InstancePrefix}{Guid.NewGuid():N}";
        await SetActiveResetAsync(resetId, cancellationToken);
        await workflows.ScheduleNewWorkflowAsync(nameof(AirportResetWorkflow), resetId,
            new AirportResetRequest(resetId, DateTimeOffset.UtcNow),
            startTime: null, cancellation: cancellationToken);
        var completed = await workflows.WaitForWorkflowCompletionAsync(resetId,
            getInputsAndOutputs: false, cancellation: cancellationToken);
        if (completed.RuntimeStatus != WorkflowRuntimeStatus.Completed)
            throw new InvalidOperationException($"ATC did not acknowledge airport reset {resetId}.");
        // Drain in-flight acknowledgements before purging, then ignore any redelivery.
        await SetActiveResetAsync(null, cancellationToken);
        if (!await workflows.PurgeInstanceAsync(resetId, cancellationToken))
            throw new InvalidOperationException($"Could not purge reset workflow {resetId}.");

        await dapr.DeleteStateAsync(DaprTopics.StateStoreName, WeatherSnapshotStore.StateKey,
            cancellationToken: cancellationToken);
        await dapr.DeleteStateAsync(DaprTopics.StateStoreName, FlightOps.IndexKey,
            cancellationToken: cancellationToken);
        logger.LogInformation("Cleared all airport flight, clearance and cached weather state");
    }

    private static bool IsResetWorkflow(string id) =>
        id.StartsWith(AirportResetWorkflow.InstancePrefix, StringComparison.Ordinal);

    private async Task<WorkflowState> StopAndPurgeAsync(string instanceId, CancellationToken cancellationToken)
    {
        var state = await workflows.GetWorkflowStateAsync(instanceId, cancellation: cancellationToken)
            ?? throw new InvalidOperationException($"Could not inspect workflow {instanceId}.");
        if (state.RuntimeStatus is not (WorkflowRuntimeStatus.Completed or
            WorkflowRuntimeStatus.Failed or WorkflowRuntimeStatus.Terminated))
        {
            await workflows.TerminateWorkflowAsync(instanceId, "Airport state reset", cancellationToken);
            await workflows.WaitForWorkflowCompletionAsync(instanceId,
                getInputsAndOutputs: false, cancellation: cancellationToken);
        }
        if (!await workflows.PurgeInstanceAsync(instanceId, cancellationToken))
            throw new InvalidOperationException($"Could not purge workflow {instanceId}.");
        return state;
    }

    private async Task SetActiveResetAsync(string? instanceId, CancellationToken cancellationToken)
    {
        await _acknowledgements.WaitAsync(cancellationToken);
        try
        {
            _activeResetId = instanceId;
        }
        finally
        {
            _acknowledgements.Release();
        }
    }

    public void Dispose() => _acknowledgements.Dispose();
}
