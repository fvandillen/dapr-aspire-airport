namespace Airport.Contracts;

/// <summary>Kind of clearance the flight crew is asking for.</summary>
public enum ClearanceKind
{
    Takeoff,
    Landing
}

/// <summary>FlightOperations -> ATC: please clear this flight for takeoff or landing.</summary>
public sealed record ClearanceRequest(
    string FlightId,
    string Callsign,
    ClearanceKind Kind,
    DateTimeOffset RequestedAt);

/// <summary>ATC -> FlightOperations: the decision for a previously requested clearance.</summary>
public sealed record ClearanceResult(
    string FlightId,
    string Callsign,
    ClearanceKind Kind,
    bool Granted,
    string Runway,
    string Reason,
    DateTimeOffset DecidedAt);

/// <summary>Stored in the state store under <c>active-clearances</c>; what ATC currently has on its plate.</summary>
public sealed record ActiveClearance(
    string FlightId,
    string Callsign,
    ClearanceKind Kind,
    string Runway,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt);

/// <summary>UI -> FlightOperations: force-grant or force-deny the pending clearance for a flight.</summary>
public sealed record ForceClearanceRequest(
    ClearanceKind Kind,
    bool Granted,
    string? Runway = null);
