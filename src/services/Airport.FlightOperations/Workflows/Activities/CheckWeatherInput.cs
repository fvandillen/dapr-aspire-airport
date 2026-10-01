namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Marker input record for the weather check (no payload, but needed to avoid the
/// signature clash with the base <c>WorkflowActivity.RunAsync(object?)</c> overload).</summary>
public sealed record CheckWeatherInput;
