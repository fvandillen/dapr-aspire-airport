using System.Threading.Channels;
using Airport.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Airport.Web.Services;

internal sealed class AirportLiveFeed : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<TaskCompletionSource?> _requests =
        Channel.CreateUnbounded<TaskCompletionSource?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<CancellationToken, Task> _read;
    private readonly Action<string?> _connectionError;
    private readonly ILogger _logger;
    private readonly string _name;
    private Task? _connecting;
    private Task? _refreshing;

    public AirportLiveFeed(Uri serviceUri, string name, Func<CancellationToken, Task> read,
        Action<string?> connectionError, ILogger logger)
    {
        _name = name;
        _read = read;
        _connectionError = connectionError;
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(serviceUri, AirportUpdates.HubPath), options =>
            {
                // Never fall back to long polling: idle HTTP requests obscure the demo traces.
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .WithAutomaticReconnect(new ReconnectPolicy())
            .Build();
        _connection.On(AirportUpdates.Changed, () => RequestRefresh());
        _connection.Reconnecting += error =>
        {
            ReportDisconnect(error);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _connectionError(null);
            RequestRefresh();
            return Task.CompletedTask;
        };
    }

    public void Start()
    {
        if (_refreshing is not null) return;
        _connectionError($"Connecting to {_name} live updates. Any previous snapshot is retained.");
        _refreshing = ReadChangesAsync();
        _connecting = ConnectAsync();
    }

    private void RequestRefresh() => _requests.Writer.TryWrite(null);

    public Task RefreshAsync()
    {
        if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
        Start();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(completion))
            completion.TrySetCanceled(_lifetime.Token);
        return completion.Task;
    }

    private async Task ReadChangesAsync()
    {
        var waiting = new List<TaskCompletionSource>();
        try
        {
            while (await _requests.Reader.WaitToReadAsync(_lifetime.Token))
            {
                while (_requests.Reader.TryRead(out var completion))
                    if (completion is not null) waiting.Add(completion);

                // Requests received during a read remain queued for a subsequent snapshot.
                await _read(_lifetime.Token);
                foreach (var completion in waiting) completion.TrySetResult();
                waiting.Clear();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The workspace is closing.
        }
        finally
        {
            foreach (var completion in waiting) completion.TrySetCanceled();
            while (_requests.Reader.TryRead(out var completion))
                completion?.TrySetCanceled();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    // Connect before the snapshot so changes during initial loading cannot be missed.
                    await _connection.StartAsync(timeout.Token);
                    _connectionError(null);
                    RequestRefresh();
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or
                    OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
                {
                    if (_lifetime.IsCancellationRequested) return;
                    ReportDisconnect(ex);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Initial connection retries stop with the workspace.
        }
    }

    private void ReportDisconnect(Exception? error)
    {
        if (_lifetime.IsCancellationRequested) return;
        _logger.LogWarning(error, "{Feed} live connection lost; reconnecting", _name);
        _connectionError($"{_name} live updates disconnected. Any previous snapshot is retained; reconnecting automatically.");
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _requests.Writer.TryComplete();
        await _connection.DisposeAsync();
        if (_connecting is not null) await _connecting;
        if (_refreshing is not null) await _refreshing;
        _lifetime.Dispose();
    }

    private sealed class ReconnectPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            TimeSpan.FromSeconds(retryContext.PreviousRetryCount == 0 ? 0 : 5);
    }
}
