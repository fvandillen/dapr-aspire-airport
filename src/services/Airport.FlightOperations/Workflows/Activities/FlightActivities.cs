using System.Net.Http.Json;
using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Dapr.Actors;
using Dapr.Actors.Client;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows.Activities;

/// <summary>Creates the Aircraft actor and seeds its state for a freshly scheduled flight.</summary>
public sealed class InitializeAircraftActivity(ILogger<InitializeAircraftActivity> logger)
    : WorkflowActivity<AircraftInitData, bool>
{
    public override async Task<bool> RunAsync(WorkflowActivityContext context, AircraftInitData data)
    {
        var actor = AircraftActorProxy.For(data.FlightId);
        await actor.InitializeAsync(data);
        logger.LogInformation("Initialized aircraft actor for flight {FlightId}", data.FlightId);
        return true;
    }
}

/// <summary>Transitions the Aircraft actor to a new status (with optional runway / timestamps).</summary>
public sealed class UpdateAircraftStatusActivity(ILogger<UpdateAircraftStatusActivity> logger)
    : WorkflowActivity<UpdateAircraftStatusActivity.Input, bool>
{
    public sealed record Input(string FlightId, StatusUpdate Update);

    public override async Task<bool> RunAsync(WorkflowActivityContext context, Input input)
    {
        var actor = AircraftActorProxy.For(input.FlightId);
        await actor.UpdateStatusAsync(input.Update);
        logger.LogDebug("Flight {FlightId} -> {Status}", input.FlightId, input.Update.Status);
        return true;
    }
}

/// <summary>Publishes a ClearanceRequest on the Dapr pub/sub for ATC to pick up.</summary>
public sealed class RequestClearanceActivity(DaprClient dapr, ILogger<RequestClearanceActivity> logger)
    : WorkflowActivity<ClearanceRequest, bool>
{
    public override async Task<bool> RunAsync(WorkflowActivityContext context, ClearanceRequest request)
    {
        await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.ClearanceRequests, request);
        logger.LogInformation("Published {Kind} clearance request for {Callsign}", request.Kind, request.Callsign);
        return true;
    }
}

/// <summary>Marker input record for the weather check (no payload, but needed to avoid the
/// signature clash with the base <c>WorkflowActivity.RunAsync(object?)</c> overload).</summary>
public sealed record CheckWeatherInput;

/// <summary>Calls the WeatherService via Dapr service invocation. Demos the invoke building block.</summary>
public sealed class CheckWeatherActivity(ILogger<CheckWeatherActivity> logger)
    : WorkflowActivity<CheckWeatherInput, WeatherSnapshot>
{
    public override async Task<WeatherSnapshot> RunAsync(WorkflowActivityContext context, CheckWeatherInput _)
    {
        // Modern (non-obsolete) service invocation: a routed HttpClient pointed at the sidecar.
        using var client = DaprClient.CreateInvokeHttpClient(appId: "weather-service");
        var snapshot = await client.GetFromJsonAsync<WeatherSnapshot>("/weather")
            ?? throw new InvalidOperationException("WeatherService returned no snapshot");
        logger.LogInformation("Weather check: {Condition}, flyable={Flyable}",
            snapshot.Condition, snapshot.IsFlyable);
        return snapshot;
    }
}
