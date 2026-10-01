using Airport.Contracts;
using Dapr.Client;

namespace Airport.FlightOperations;

public sealed class FlightStateGate(DaprClient dapr, ILogger<FlightStateGate> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate);
    }

    public async Task<bool> ContainsAsync(string flightId, CancellationToken cancellationToken = default)
    {
        var ids = await dapr.GetStateAsync<List<string>>(DaprTopics.StateStoreName, FlightOps.IndexKey,
            cancellationToken: cancellationToken) ?? [];
        var exists = ids.Contains(flightId);
        if (!exists)
            logger.LogInformation("Ignoring request or activity for removed flight {FlightId}", flightId);
        return exists;
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
