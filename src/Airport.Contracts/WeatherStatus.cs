namespace Airport.Contracts;

/// <summary>Diagnostic + control state of the WeatherService, returned by <c>GET /weather/status</c>.</summary>
public sealed record WeatherStatus(
    WeatherSnapshot Current,
    bool Paused,
    bool OverrideActive,
    string? PresetName,
    int PublishIntervalSeconds);

/// <summary>
/// Named preset snapshots used by both the demo UI and the WeatherService.
/// Keep names lowercase so URL routes stay simple.
/// </summary>
public static class WeatherPresets
{
    public static WeatherSnapshot Cavok() => new(
        DateTimeOffset.UtcNow, "CAVOK", 18, WindKnots: 4, WindDirectionDegrees: 270,
        VisibilityMeters: 10_000, CloudBaseFeet: 5_000);

    public static WeatherSnapshot LightRain() => new(
        DateTimeOffset.UtcNow, "Light rain", 12, WindKnots: 10, WindDirectionDegrees: 240,
        VisibilityMeters: 6_000, CloudBaseFeet: 2_500);

    public static WeatherSnapshot Windy() => new(
        DateTimeOffset.UtcNow, "Gusty winds", 14, WindKnots: 40, WindDirectionDegrees: 220,
        VisibilityMeters: 8_000, CloudBaseFeet: 3_500);

    public static WeatherSnapshot Foggy() => new(
        DateTimeOffset.UtcNow, "Fog", 6, WindKnots: 2, WindDirectionDegrees: 0,
        VisibilityMeters: 600, CloudBaseFeet: 200);

    public static WeatherSnapshot Thunderstorms() => new(
        DateTimeOffset.UtcNow, "Thunderstorms", 16, WindKnots: 30, WindDirectionDegrees: 200,
        VisibilityMeters: 1_200, CloudBaseFeet: 250);

    public static WeatherSnapshot Snow() => new(
        DateTimeOffset.UtcNow, "Snow", -3, WindKnots: 18, WindDirectionDegrees: 30,
        VisibilityMeters: 1_000, CloudBaseFeet: 400);

    /// <summary>Looks up a preset by name (case-insensitive). Returns null for unknown names.</summary>
    public static WeatherSnapshot? TryGet(string name) => name?.ToLowerInvariant() switch
    {
        "cavok"         => Cavok(),
        "lightrain"     => LightRain(),
        "windy"         => Windy(),
        "foggy"         => Foggy(),
        "thunderstorms" => Thunderstorms(),
        "snow"          => Snow(),
        _               => null,
    };
}
