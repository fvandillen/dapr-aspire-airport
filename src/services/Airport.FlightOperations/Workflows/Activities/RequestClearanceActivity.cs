using System.Diagnostics;
using Airport.Contracts;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Publishes a ClearanceRequest on the Dapr pub/sub for ATC to pick up.</summary>
public sealed class RequestClearanceActivity(
    DaprClient dapr, FlightStateGate gate, ILogger<RequestClearanceActivity> logger)
    : WorkflowActivity<ClearanceRequest, bool>
{
    public override async Task<bool> RunAsync(WorkflowActivityContext context, ClearanceRequest request)
    {
        using var lease = await gate.EnterAsync();
        if (!await gate.ContainsAsync(request.FlightId))
            return false;
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.request_clearance",
            ActivityKind.Producer,
            parentContext: FlightTrace.ContextFor(request.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"request {request.Kind} clearance ({request.Callsign})";
            span.SetTag("flight.id", request.FlightId);
            span.SetTag("flight.callsign", request.Callsign);
            span.SetTag("clearance.kind", request.Kind.ToString());
        }

        await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.ClearanceRequests, request);
        logger.LogInformation("Published {Kind} clearance request for {Callsign}", request.Kind, request.Callsign);
        return true;
    }
}
