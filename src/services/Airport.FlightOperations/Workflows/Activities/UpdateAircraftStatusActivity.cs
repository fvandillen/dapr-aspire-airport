using System.Diagnostics;
using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Transitions the Aircraft actor to a new status (with optional runway / timestamps).</summary>
public sealed class UpdateAircraftStatusActivity(FlightStateGate gate, ILogger<UpdateAircraftStatusActivity> logger)
    : WorkflowActivity<UpdateAircraftStatusActivity.Input, bool>
{
    public sealed record Input(string FlightId, StatusUpdate Update);

    public override async Task<bool> RunAsync(WorkflowActivityContext context, Input input)
    {
        using var lease = await gate.EnterAsync();
        if (!await gate.ContainsAsync(input.FlightId))
            return false;
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.update_status",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"status → {input.Update.Status}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.status", input.Update.Status.ToString());
            if (input.Update.Runway is not null) span.SetTag("flight.runway", input.Update.Runway);
            if (input.Update.Note is not null) span.SetTag("flight.note", input.Update.Note);
        }

        var actor = AircraftActorProxy.For(input.FlightId);
        await actor.UpdateStatusAsync(input.Update);
        logger.LogDebug("Flight {FlightId} -> {Status}", input.FlightId, input.Update.Status);
        return true;
    }
}
