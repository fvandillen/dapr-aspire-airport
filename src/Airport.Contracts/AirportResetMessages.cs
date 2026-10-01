namespace Airport.Contracts;

public sealed record AirportResetRequest(string WorkflowId, DateTimeOffset ResetAt);

public sealed record AirportResetCompleted(string WorkflowId);
