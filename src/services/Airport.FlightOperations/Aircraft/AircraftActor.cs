using Airport.ServiceDefaults;
using Dapr.Actors.Runtime;

namespace Airport.FlightOperations.Aircraft;

/// <summary>
/// Aircraft actor implementation. State persists in the Dapr state store flagged with
/// <c>actorStateStore: "true"</c> (see <c>dapr/components/statestore.yaml</c>).
/// </summary>
public sealed class AircraftActor(ActorHost host, AirportUpdateNotifier updates) : Actor(host), IAircraftActor
{
    private const string StateKey = "state";

    public async Task InitializeAsync(AircraftInitData data)
    {
        var state = new AircraftState
        {
            FlightId = data.FlightId,
            Callsign = data.Callsign,
            Origin = data.Origin,
            Destination = data.Destination,
            AircraftType = data.AircraftType,
            Gate = data.Gate,
            ScheduledDeparture = data.ScheduledDeparture,
            Status = Airport.Contracts.FlightStatus.Scheduled,
        };
        await StateManager.SetStateAsync(StateKey, state);
        await StateManager.SaveStateAsync();
        await updates.ChangedAsync();
        Logger.LogInformation("Aircraft {Callsign} ({FlightId}) initialized", data.Callsign, data.FlightId);
    }

    public async Task UpdateStatusAsync(StatusUpdate update)
    {
        var current = await StateManager.TryGetStateAsync<AircraftState>(StateKey);
        if (!current.HasValue)
        {
            Logger.LogWarning("UpdateStatusAsync called on uninitialized aircraft {ActorId}", Id);
            return;
        }

        var next = current.Value with
        {
            Status = update.Status,
            Runway = update.Runway ?? current.Value.Runway,
            ActualDeparture = update.ActualDeparture ?? current.Value.ActualDeparture,
            ActualArrival = update.ActualArrival ?? current.Value.ActualArrival,
            Note = update.Note ?? current.Value.Note,
        };

        if (next == current.Value)
            return;

        await StateManager.SetStateAsync(StateKey, next);
        // A notification must never expose an uncommitted actor snapshot.
        await StateManager.SaveStateAsync();
        await updates.ChangedAsync();
        Logger.LogInformation("Aircraft {Callsign}: status -> {Status} (runway {Runway})",
            next.Callsign, next.Status, next.Runway ?? "-");
    }

    public async Task<AircraftState> GetStateAsync()
    {
        var current = await StateManager.TryGetStateAsync<AircraftState>(StateKey);
        return current.HasValue ? current.Value : new AircraftState { FlightId = Id.GetId() };
    }

    public async Task ClearStateAsync()
    {
        await StateManager.TryRemoveStateAsync(StateKey);
        await StateManager.SaveStateAsync();
        await updates.ChangedAsync();
        Logger.LogInformation("Cleared aircraft state for {FlightId}", Id);
    }
}
