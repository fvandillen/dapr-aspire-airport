using Airport.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Airport.ServiceDefaults;

public sealed class AirportUpdatesHub : Hub;

public sealed class AirportUpdateNotifier(IHubContext<AirportUpdatesHub> hub)
{
    public Task ChangedAsync(CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(AirportUpdates.Changed, cancellationToken);
}
