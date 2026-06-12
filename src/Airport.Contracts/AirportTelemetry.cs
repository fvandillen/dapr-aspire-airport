using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Airport.Contracts;

/// <summary>
/// Single source of truth for the demo's custom OpenTelemetry instruments.
/// All services register this ActivitySource + Meter through ServiceDefaults so the
/// Aspire dashboard groups every custom span / metric under one logical name.
/// </summary>
public static class AirportTelemetry
{
    /// <summary>OTel source name; matches the prefix used in the dashboard's instrument filters.</summary>
    public const string Name = "Airport";

    /// <summary>Spans produced by services and the workflow.</summary>
    public static readonly ActivitySource Source = new(Name);

    /// <summary>Meter used by all metrics below.</summary>
    public static readonly Meter Meter = new(Name);

    // ---- Weather metrics ---------------------------------------------------

    /// <summary>Total weather snapshots published. Tagged with <c>weather.condition</c> and <c>weather.flyable</c>.</summary>
    public static readonly Counter<long> WeatherPublished =
        Meter.CreateCounter<long>("airport.weather.published", unit: "{snapshot}",
            description: "Weather snapshots emitted on the weather-updates pub/sub topic.");

    // ---- ATC metrics -------------------------------------------------------

    /// <summary>Clearance decisions made by ATC. Tagged with <c>clearance.kind</c>, <c>clearance.granted</c> and (when granted) <c>runway</c>.</summary>
    public static readonly Counter<long> ClearanceDecisions =
        Meter.CreateCounter<long>("airport.atc.clearance_decisions", unit: "{decision}",
            description: "Clearance decisions ATC has made (granted or denied).");

    /// <summary>How long ATC took to decide on a clearance (milliseconds).</summary>
    public static readonly Histogram<double> ClearanceDecisionDurationMs =
        Meter.CreateHistogram<double>("airport.atc.clearance_decision_duration", unit: "ms",
            description: "Wall-clock time spent deciding on a clearance request.");

    // ---- Flight workflow metrics ------------------------------------------

    /// <summary>Total flights scheduled (workflow started).</summary>
    public static readonly Counter<long> FlightsScheduled =
        Meter.CreateCounter<long>("airport.flights.scheduled", unit: "{flight}",
            description: "Flight workflows started.");

    /// <summary>Total flights landed (workflow completed successfully).</summary>
    public static readonly Counter<long> FlightsLanded =
        Meter.CreateCounter<long>("airport.flights.landed", unit: "{flight}",
            description: "Flight workflows that reached the Landed state.");

    /// <summary>Total flights cancelled (workflow returned a failure outcome). Tagged with <c>reason</c>.</summary>
    public static readonly Counter<long> FlightsCancelled =
        Meter.CreateCounter<long>("airport.flights.cancelled", unit: "{flight}",
            description: "Flight workflows that ended in Cancelled.");

    /// <summary>Seconds spent waiting for a gate lock before boarding. Tagged with <c>gate</c>.</summary>
    public static readonly Histogram<double> GateWaitSeconds =
        Meter.CreateHistogram<double>("airport.flights.gate_wait_seconds", unit: "s",
            description: "Time the workflow spent waiting on the gate distributed lock.");

    /// <summary>End-to-end flight duration (Scheduled -> Landed/Cancelled) in seconds.</summary>
    public static readonly Histogram<double> FlightDurationSeconds =
        Meter.CreateHistogram<double>("airport.flights.duration_seconds", unit: "s",
            description: "End-to-end flight workflow duration.");
}
