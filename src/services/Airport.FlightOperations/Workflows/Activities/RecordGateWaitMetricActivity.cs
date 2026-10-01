using System.Diagnostics;
using Airport.Contracts;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Records the <c>airport.flights.gate_wait_seconds</c> histogram.</summary>
public sealed class RecordGateWaitMetricActivity : WorkflowActivity<GateWaitMetric, bool>
{
    public override Task<bool> RunAsync(WorkflowActivityContext context, GateWaitMetric metric)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.record_gate_wait",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(metric.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"record gate-wait {metric.Seconds:0.0}s ({metric.Gate})";
            span.SetTag("flight.id", metric.FlightId);
            span.SetTag("flight.gate", metric.Gate);
            span.SetTag("gate.wait_seconds", metric.Seconds);
        }

        AirportTelemetry.GateWaitSeconds.Record(metric.Seconds,
            new KeyValuePair<string, object?>("gate", metric.Gate));
        return Task.FromResult(true);
    }
}
