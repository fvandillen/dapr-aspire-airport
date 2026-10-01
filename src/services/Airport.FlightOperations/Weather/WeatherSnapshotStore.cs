using Airport.Contracts;
using Dapr.Client;

namespace Airport.FlightOperations.Weather;

public sealed class WeatherSnapshotStore(DaprClient dapr, ILogger<WeatherSnapshotStore> logger) : IDisposable
{
    public const string StateKey = "latest-weather";
    private readonly SemaphoreSlim _updates = new(1, 1);

    public Task<WeatherSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        dapr.GetStateAsync<WeatherSnapshot?>(DaprTopics.StateStoreName, StateKey,
            cancellationToken: cancellationToken);

    public async Task<bool> ObserveAsync(WeatherSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Pub/sub can deliver concurrent, duplicate, or out-of-order snapshots.
        await _updates.WaitAsync(cancellationToken);
        try
        {
            var current = await GetLatestAsync(cancellationToken);
            if (current is not null && current.ObservedAt >= snapshot.ObservedAt)
            {
                logger.LogDebug("Retaining weather from {ObservedAt}; received snapshot from {IncomingObservedAt}",
                    current.ObservedAt, snapshot.ObservedAt);
                return current.ObservedAt == snapshot.ObservedAt;
            }

            await dapr.SaveStateAsync(DaprTopics.StateStoreName, StateKey, snapshot,
                cancellationToken: cancellationToken);
            logger.LogInformation("Weather event observed: {Condition}, flyable={Flyable}",
                snapshot.Condition, snapshot.IsFlyable);
            return true;
        }
        finally
        {
            _updates.Release();
        }
    }

    public void Dispose() => _updates.Dispose();
}
