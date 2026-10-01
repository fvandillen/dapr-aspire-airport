using System.Text.Json;
using Airport.Contracts;

namespace Airport.Web.Services;

public sealed class AirportLiveState : IAsyncDisposable
{
    private readonly AirportLiveFeed _flights;
    private readonly AirportLiveFeed _weather;
    private readonly AirportLiveFeed _tower;
    private readonly ILogger<AirportLiveState> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _clearanceExpiry;
    private string? _flightsReadError;
    private string? _weatherReadError;
    private string? _towerReadError;
    private string? _towerWeatherReadError;
    private string? _flightsConnectionError;
    private string? _weatherConnectionError;
    private string? _towerConnectionError;
    private long _revision;

    public AirportLiveState(FlightsApi flights, WeatherApi weather, AtcApi tower,
        ILogger<AirportLiveState> logger)
    {
        _logger = logger;
        _flights = new(flights.ServiceUri, "Flight operations", async ct =>
        {
            await ReadAsync("Flight operations", flights.GetAllAsync, value =>
            {
                Flights = value;
                FlightsUpdatedAt = DateTimeOffset.UtcNow;
            }, error => _flightsReadError = error, ct);
            NotifyChanged();
        }, error => { _flightsConnectionError = error; NotifyChanged(); }, logger);
        _weather = new(weather.ServiceUri, "Weather", async ct =>
        {
            await ReadAsync("Weather", weather.GetStatusAsync, value =>
            {
                Weather = value ?? throw new JsonException("The weather response was empty.");
                WeatherUpdatedAt = DateTimeOffset.UtcNow;
            }, error => _weatherReadError = error, ct);
            NotifyChanged();
        }, error => { _weatherConnectionError = error; NotifyChanged(); }, logger);
        _tower = new(tower.ServiceUri, "Tower", async ct =>
        {
            await Task.WhenAll(
                ReadAsync("Tower clearances", tower.GetActiveClearancesAsync, value =>
                {
                    Clearances = value.Where(c => c.ExpiresAt > DateTimeOffset.UtcNow).ToArray();
                    TowerUpdatedAt = DateTimeOffset.UtcNow;
                    ScheduleClearanceExpiry();
                }, error => _towerReadError = error, ct),
                ReadAsync("Tower weather", tower.GetLastSeenWeatherAsync,
                    value => TowerWeather = value, error => _towerWeatherReadError = error, ct));
            NotifyChanged();
        }, error => { _towerConnectionError = error; NotifyChanged(); }, logger);
    }

    public IReadOnlyList<FlightView> Flights { get; private set; } = [];
    public IReadOnlyList<ActiveClearance> Clearances { get; private set; } = [];
    public WeatherStatus? Weather { get; private set; }
    public WeatherSnapshot? TowerWeather { get; private set; }
    public DateTimeOffset? FlightsUpdatedAt { get; private set; }
    public DateTimeOffset? WeatherUpdatedAt { get; private set; }
    public DateTimeOffset? TowerUpdatedAt { get; private set; }
    public string? FlightsError => _flightsConnectionError ?? _flightsReadError;
    public string? WeatherError => _weatherConnectionError ?? _weatherReadError;
    public string? TowerError => _towerConnectionError ?? _towerReadError;
    public string? TowerWeatherError => _towerConnectionError ?? _towerWeatherReadError;
    public long Revision => _revision;
    public bool FlightsLive => FlightsUpdatedAt is not null && FlightsError is null;
    public bool WeatherLive => WeatherUpdatedAt is not null && WeatherError is null;
    public bool TowerLive => TowerUpdatedAt is not null && TowerError is null;
    public IEnumerable<ActiveClearance> ActiveClearances =>
        Clearances.Where(c => c.ExpiresAt > DateTimeOffset.UtcNow);
    public event Action? Changed;

    public void Start()
    {
        _flights.Start();
        _weather.Start();
        _tower.Start();
    }

    public Task RefreshAsync() =>
        Task.WhenAll(RefreshFlightsAsync(), RefreshWeatherAsync(), RefreshTowerAsync());

    public Task RefreshFlightsAsync() => _flights.RefreshAsync();
    public Task RefreshWeatherAsync() => _weather.RefreshAsync();
    public Task RefreshTowerAsync() => _tower.RefreshAsync();

    private void NotifyChanged()
    {
        if (_lifetime.IsCancellationRequested) return;
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private void ScheduleClearanceExpiry()
    {
        if (_lifetime.IsCancellationRequested) return;
        _clearanceExpiry ??= new Timer(_ =>
        {
            if (_lifetime.IsCancellationRequested) return;
            Clearances = ActiveClearances.ToArray();
            NotifyChanged();
            ScheduleClearanceExpiry();
        });
        var next = Clearances.MinBy(c => c.ExpiresAt);
        var delay = next is null ? Timeout.InfiniteTimeSpan : next.ExpiresAt - DateTimeOffset.UtcNow;
        _clearanceExpiry.Change(delay < TimeSpan.Zero && next is not null ? TimeSpan.Zero : delay,
            Timeout.InfiniteTimeSpan);
    }

    private async Task ReadAsync<T>(
        string feed, Func<CancellationToken, Task<T>> read, Action<T> accept,
        Action<string?> setError, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            accept(await read(timeout.Token));
            setError(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is expected during disposal; preserve the last snapshot.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Feed} feed could not be refreshed", feed);
            setError($"{feed} unavailable. Any previous snapshot is retained; use Refresh to retry.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_clearanceExpiry is not null) await _clearanceExpiry.DisposeAsync();
        await Task.WhenAll(_flights.DisposeAsync().AsTask(), _weather.DisposeAsync().AsTask(),
            _tower.DisposeAsync().AsTask());
        _lifetime.Dispose();
    }
}
