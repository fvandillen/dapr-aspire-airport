using Dapr.Actors;
using Dapr.Actors.Client;

namespace Airport.FlightOperations.Aircraft;

/// <summary>
/// Helper that builds Aircraft actor proxies with JSON serialization enabled.
/// </summary>
/// <remarks>
/// Dapr Actor remoting defaults to <c>DataContractSerializer</c> (XML), which can't
/// serialize positional records (they have no parameterless constructor). Flipping
/// the proxy to JSON keeps our existing record DTOs working as-is.
/// </remarks>
internal static class AircraftActorProxy
{
    private static readonly ActorProxyOptions s_options = new()
    {
        UseJsonSerialization = true,
    };

    public static IAircraftActor For(string flightId) =>
        ActorProxy.Create<IAircraftActor>(
            new ActorId(flightId),
            nameof(AircraftActor),
            s_options);
}
