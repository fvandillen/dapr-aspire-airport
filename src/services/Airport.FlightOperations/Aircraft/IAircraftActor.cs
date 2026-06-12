using Airport.Contracts;
using Dapr.Actors;

namespace Airport.FlightOperations.Aircraft;

/// <summary>
/// Public surface of the Aircraft actor.
/// Each flight maps to an actor instance whose id is the FlightId.
/// </summary>
public interface IAircraftActor : IActor
{
    Task InitializeAsync(AircraftInitData data);

    Task UpdateStatusAsync(StatusUpdate update);

    Task<AircraftState> GetStateAsync();
}

/// <summary>One-time initialization payload, sent by the workflow when a flight is scheduled.</summary>
public sealed record AircraftInitData(
    string FlightId,
    string Callsign,
    string Origin,
    string Destination,
    string AircraftType,
    string Gate,
    DateTimeOffset ScheduledDeparture);

/// <summary>Partial-update payload used by the workflow at every lifecycle transition.</summary>
public sealed record StatusUpdate(
    FlightStatus Status,
    string? Runway = null,
    DateTimeOffset? ActualDeparture = null,
    DateTimeOffset? ActualArrival = null,
    string? Note = null);

/// <summary>Snapshot of an aircraft's state, persisted in the Dapr actor state store.</summary>
public sealed record AircraftState
{
    public string FlightId { get; init; } = "";
    public string Callsign { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Destination { get; init; } = "";
    public string AircraftType { get; init; } = "";
    public string Gate { get; init; } = "";
    public DateTimeOffset ScheduledDeparture { get; init; }
    public DateTimeOffset? ActualDeparture { get; init; }
    public DateTimeOffset? ActualArrival { get; init; }
    public FlightStatus Status { get; init; } = FlightStatus.Scheduled;
    public string? Runway { get; init; }
    public string? Note { get; init; }
}
