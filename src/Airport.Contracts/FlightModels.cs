namespace Airport.Contracts;

/// <summary>Phases an Aircraft moves through during a flight workflow.</summary>
public enum FlightStatus
{
    Scheduled,
    BoardingPushback,
    AwaitingTakeoffClearance,
    Departed,
    Cruising,
    AwaitingLandingClearance,
    Landed,
    Cancelled
}

/// <summary>Public read-model returned by FlightOperations for the departure board / detail page.</summary>
public sealed record FlightView(
    string FlightId,
    string Callsign,
    string Origin,
    string Destination,
    string AircraftType,
    DateTimeOffset ScheduledDeparture,
    DateTimeOffset? ActualDeparture,
    DateTimeOffset? ActualArrival,
    FlightStatus Status,
    string Gate,
    string? Runway,
    string? Note);

/// <summary>Body for POST /flights when scheduling a new flight from the UI.</summary>
public sealed record ScheduleFlightRequest(
    string Callsign,
    string Origin,
    string Destination,
    string AircraftType,
    string Gate,
    DateTimeOffset ScheduledDeparture);

/// <summary>Workflow input record. Plain-old-data so Dapr can serialize it.</summary>
public sealed record FlightWorkflowInput(
    string FlightId,
    string Callsign,
    string Origin,
    string Destination,
    string AircraftType,
    string Gate);
