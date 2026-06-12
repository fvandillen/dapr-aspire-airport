namespace Airport.Contracts;

/// <summary>
/// Snapshot of current weather published by the WeatherService.
/// </summary>
public sealed record WeatherSnapshot(
    DateTimeOffset ObservedAt,
    string Condition,
    double TemperatureCelsius,
    int WindKnots,
    int WindDirectionDegrees,
    int VisibilityMeters,
    int CloudBaseFeet)
{
    /// <summary>True when conditions are above ATC takeoff/landing minima.</summary>
    public bool IsFlyable =>
        WindKnots < 35
        && VisibilityMeters >= 1500
        && CloudBaseFeet >= 300;
}
