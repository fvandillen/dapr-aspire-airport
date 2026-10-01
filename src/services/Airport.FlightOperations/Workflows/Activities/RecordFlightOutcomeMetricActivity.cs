using System.Diagnostics;
using Airport.Contracts;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Records flight-lifetime metrics once when a workflow reaches a terminal state.</summary>
public sealed class RecordFlightOutcomeMetricActivity : WorkflowActivity<FlightOutcomeMetric, bool>
{
    public override Task<bool> RunAsync(WorkflowActivityContext context, FlightOutcomeMetric metric)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.record_outcome",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(metric.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"outcome: {metric.Outcome} ({metric.DurationSeconds:0.0}s)";
            span.SetTag("flight.id", metric.FlightId);
            span.SetTag("flight.outcome", metric.Outcome.ToString());
            if (metric.Reason is not null) span.SetTag("flight.cancel_reason", metric.Reason);
            span.SetTag("flight.duration_seconds", metric.DurationSeconds);
        }

        AirportTelemetry.FlightDurationSeconds.Record(metric.DurationSeconds,
            new KeyValuePair<string, object?>("outcome", metric.Outcome.ToString()));

        if (metric.Outcome is FlightStatus.Landed)
        {
            AirportTelemetry.FlightsLanded.Add(1);
        }
        else if (metric.Outcome is FlightStatus.Cancelled)
        {
            AirportTelemetry.FlightsCancelled.Add(1,
                new KeyValuePair<string, object?>("reason", metric.Reason ?? "unknown"));
        }
        return Task.FromResult(true);
    }
}
