using System.Diagnostics;
using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Creates the Aircraft actor and seeds its state for a freshly scheduled flight.</summary>
public sealed class InitializeAircraftActivity(FlightStateGate gate, ILogger<InitializeAircraftActivity> logger)
    : WorkflowActivity<AircraftInitData, bool>
{
    public override async Task<bool> RunAsync(WorkflowActivityContext context, AircraftInitData data)
    {
        using var lease = await gate.EnterAsync();
        if (!await gate.ContainsAsync(data.FlightId))
            return false;
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.initialize_aircraft",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(data.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"init aircraft {data.Callsign}";
            span.SetTag("flight.id", data.FlightId);
            span.SetTag("flight.callsign", data.Callsign);
            span.SetTag("flight.gate", data.Gate);
        }

        var actor = AircraftActorProxy.For(data.FlightId);
        await actor.InitializeAsync(data);
        logger.LogInformation("Initialized aircraft actor for flight {FlightId}", data.FlightId);
        return true;
    }
}
