using Airport.Contracts;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Workflow -> metrics: terminal flight outcome (Landed / Cancelled) and total wall-clock duration.</summary>
public sealed record FlightOutcomeMetric(string FlightId, FlightStatus Outcome, string? Reason, double DurationSeconds);
