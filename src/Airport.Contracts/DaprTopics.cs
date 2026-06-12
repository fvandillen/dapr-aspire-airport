namespace Airport.Contracts;

/// <summary>
/// Topic / pubsub component names shared between publishers and subscribers.
/// Keep these strings in one place so wiring is impossible to misspell.
/// </summary>
public static class DaprTopics
{
    /// <summary>Name of the Dapr pub/sub component (matches <c>dapr/components/pubsub.yaml</c>).</summary>
    public const string PubSubName = "pubsub";

    /// <summary>Name of the Dapr state store component (matches <c>dapr/components/statestore.yaml</c>).</summary>
    public const string StateStoreName = "statestore";

    /// <summary>Weather snapshots published periodically by the WeatherService.</summary>
    public const string WeatherUpdates = "weather-updates";

    /// <summary>Clearance requests sent from FlightOperations to ATC.</summary>
    public const string ClearanceRequests = "clearance-requests";

    /// <summary>Clearance decisions (granted / denied) published by ATC.</summary>
    public const string ClearanceResults = "clearance-results";
}
