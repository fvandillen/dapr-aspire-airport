using System.Net;
using System.Net.Http.Json;
using Airport.Contracts;

namespace Airport.Web.Services;

/// <summary>Talks to the WeatherService HTTP API.</summary>
public sealed class WeatherApi(HttpClient http)
{
    public Uri ServiceUri => http.BaseAddress ?? throw new InvalidOperationException("Weather URL is not configured.");

    public async Task<WeatherSnapshot?> GetCurrentAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<WeatherSnapshot>("/weather", ct);

    public async Task<WeatherStatus?> GetStatusAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<WeatherStatus>("/weather/status", ct);

    public async Task PauseAsync(CancellationToken ct = default) =>
        (await http.PostAsync("/weather/pause", content: null, ct)).EnsureSuccessStatusCode();

    public async Task ResumeAsync(CancellationToken ct = default) =>
        (await http.PostAsync("/weather/resume", content: null, ct)).EnsureSuccessStatusCode();

    public async Task ApplyPresetAsync(string name, CancellationToken ct = default) =>
        (await http.PostAsync($"/weather/preset/{name}", content: null, ct)).EnsureSuccessStatusCode();

    public async Task ClearOverrideAsync(CancellationToken ct = default) =>
        (await http.DeleteAsync("/weather/override", ct)).EnsureSuccessStatusCode();

    public async Task SetOverrideAsync(WeatherSnapshot snapshot, CancellationToken ct = default) =>
        (await http.PutAsJsonAsync("/weather/override", snapshot, ct)).EnsureSuccessStatusCode();
}

/// <summary>Talks to the AtcService HTTP API.</summary>
public sealed class AtcApi(HttpClient http)
{
    public Uri ServiceUri => http.BaseAddress ?? throw new InvalidOperationException("Tower URL is not configured.");

    public async Task<IReadOnlyList<ActiveClearance>> GetActiveClearancesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<ActiveClearance>>("/clearances", ct)
        ?? Array.Empty<ActiveClearance>();

    public async Task<WeatherSnapshot?> GetLastSeenWeatherAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/atc/weather", ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent
            ? null : await response.Content.ReadFromJsonAsync<WeatherSnapshot>(cancellationToken: ct);
    }
}

/// <summary>Talks to the FlightOperations HTTP API.</summary>
public sealed class FlightsApi(HttpClient http)
{
    public Uri ServiceUri => http.BaseAddress ?? throw new InvalidOperationException("Flight operations URL is not configured.");

    public async Task ClearStateAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync("/flights", ct);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ResetProblem>(cancellationToken: ct);
            throw new HttpRequestException(problem?.Detail ?? "Airport reset failed. Retry to finish cleanup.",
                inner: null, statusCode: response.StatusCode);
        }
    }

    private sealed record ResetProblem(string? Detail);

    public async Task<IReadOnlyList<FlightView>> GetAllAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<FlightView>>("/flights", ct)
        ?? Array.Empty<FlightView>();

    public async Task<FlightView?> GetAsync(string flightId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<FlightView>($"/flights/{flightId}", ct);

    public async Task<FlightView?> ScheduleAsync(ScheduleFlightRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/flights", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FlightView>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<FlightView>> SeedAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsync("/flights/seed", content: null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<FlightView>>(cancellationToken: ct)
               ?? Array.Empty<FlightView>();
    }

    /// <summary>Short-circuit the timer the workflow is currently sitting on.</summary>
    public async Task AdvanceAsync(string flightId, CancellationToken ct = default) =>
        (await http.PostAsync($"/flights/{flightId}/advance", content: null, ct)).EnsureSuccessStatusCode();

    /// <summary>Terminate the workflow and mark the aircraft as cancelled.</summary>
    public async Task CancelAsync(string flightId, CancellationToken ct = default) =>
        (await http.PostAsync($"/flights/{flightId}/cancel", content: null, ct)).EnsureSuccessStatusCode();

    /// <summary>Force-grant or force-deny the pending clearance, bypassing ATC.</summary>
    public async Task ForceClearanceAsync(
        string flightId, ClearanceKind kind, bool granted, string? runway = null, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync(
            $"/flights/{flightId}/clearance",
            new ForceClearanceRequest(kind, granted, runway),
            ct)).EnsureSuccessStatusCode();
}
