using System.Net.Http.Json;
using Airport.Contracts;

namespace Airport.Web.Services;

/// <summary>Talks to the WeatherService HTTP API.</summary>
public sealed class WeatherApi(HttpClient http)
{
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
    public async Task<IReadOnlyList<ActiveClearance>> GetActiveClearancesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<ActiveClearance>>("/clearances", ct)
        ?? Array.Empty<ActiveClearance>();

    public async Task<WeatherSnapshot?> GetLastSeenWeatherAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<WeatherSnapshot?>("/atc/weather", ct);
}

/// <summary>Talks to the FlightOperations HTTP API.</summary>
public sealed class FlightsApi(HttpClient http)
{
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
}
