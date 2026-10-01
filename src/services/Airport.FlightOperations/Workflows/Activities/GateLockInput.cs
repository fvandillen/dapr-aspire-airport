namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Input for the gate-lock activities. The workflow id is the lock owner so the
/// lock automatically releases if the workflow is terminated.</summary>
public sealed record GateLockInput(string FlightId, string Gate);
