namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Workflow -> metrics: how long the workflow had to wait on the gate distributed lock.</summary>
public sealed record GateWaitMetric(string FlightId, string Gate, double Seconds);
