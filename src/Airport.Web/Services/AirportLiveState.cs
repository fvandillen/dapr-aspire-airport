using System.Text.Json;
using Airport.Contracts;

namespace Airport.Web.Services;

public sealed class AirportLiveState(
    FlightsApi flights,
    WeatherApi weather,
    AtcApi tower,
    ILogger<AirportLiveState> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _polling;
    private Task? _refresh;

    public IReadOnlyList<FlightView> Flights { get; private set; } = [];
    public IReadOnlyList<ActiveClearance> Clearances { get; private set; } = [];
    public WeatherStatus? Weather { get; private set; }
    public WeatherSnapshot? TowerWeather { get; private set; }
    public DateTimeOffset? FlightsUpdatedAt { get; private set; }
    public DateTimeOffset? WeatherUpdatedAt { get; private set; }
    public DateTimeOffset? TowerUpdatedAt { get; private set; }
    public string? FlightsError { get; private set; }
    public string? WeatherError { get; private set; }
    public string? TowerError { get; private set; }
    public string? TowerWeatherError { get; private set; }
    public long Revision { get; private set; }
    public bool FlightsLive => FlightsUpdatedAt is not null && FlightsError is null;
    public bool WeatherLive => WeatherUpdatedAt is not null && WeatherError is null;
    public bool TowerLive => TowerUpdatedAt is not null && TowerError is null;
    public IEnumerable<ActiveClearance> ActiveClearances =>
        Clearances.Where(c => c.ExpiresAt > DateTimeOffset.UtcNow);
    public event Action? Changed;

    public void Start() => _polling ??= PollAsync();

    public Task RefreshAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return Task.CompletedTask;

        return _refresh is { IsCompleted: false } ? _refresh : _refresh = RefreshCoreAsync();
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                await RefreshAsync();
            } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The workspace is closing.
        }
    }

    private async Task RefreshCoreAsync()
    {
        await Task.WhenAll(
            ReadAsync("Flight operations", flights.GetAllAsync, value =>
            {
                Flights = value;
                FlightsUpdatedAt = DateTimeOffset.UtcNow;
            }, error => FlightsError = error),
            ReadAsync("Weather", weather.GetStatusAsync, value =>
            {
                Weather = value ?? throw new JsonException("The weather response was empty.");
                WeatherUpdatedAt = DateTimeOffset.UtcNow;
            }, error => WeatherError = error),
            ReadAsync("Tower clearances", tower.GetActiveClearancesAsync, value =>
            {
                Clearances = value;
                TowerUpdatedAt = DateTimeOffset.UtcNow;
            }, error => TowerError = error),
            ReadAsync("Tower weather", tower.GetLastSeenWeatherAsync,
                value => TowerWeather = value, error => TowerWeatherError = error));

        if (!_lifetime.IsCancellationRequested)
        {
            Revision++;
            Changed?.Invoke();
        }
    }

    private async Task ReadAsync<T>(
        string feed, Func<CancellationToken, Task<T>> read, Action<T> accept, Action<string?> setError)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            accept(await read(timeout.Token));
            setError(null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Cancellation is expected during disposal; preserve the last snapshot.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(ex, "{Feed} feed could not be refreshed", feed);
            setError($"{feed} unavailable. Any previous snapshot is retained; retrying automatically.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_polling is not null)
            await _polling;
        if (_refresh is not null)
            await _refresh;
        _lifetime.Dispose();
    }
}
