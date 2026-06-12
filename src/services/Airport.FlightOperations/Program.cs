using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Airport.FlightOperations.Workflows;
using Airport.FlightOperations.Workflows.Activities;
using Dapr;
using Dapr.Actors;
using Dapr.Actors.Client;
using Dapr.Client;
using Dapr.Workflow;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddDaprClient();

// Dapr actors: Aircraft (one per flight, state persisted in the actor state store).
// Dapr Actor remoting defaults to DataContractSerializer (XML), which can't serialize
// positional records. Flip the server side to JSON; clients use AircraftActorProxy.For(...).
builder.Services.AddActors(options =>
{
    options.UseJsonSerialization = true;
    options.Actors.RegisterActor<AircraftActor>();
});

// Dapr workflow + activities orchestrating each flight.
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<FlightWorkflow>();
    options.RegisterActivity<InitializeAircraftActivity>();
    options.RegisterActivity<UpdateAircraftStatusActivity>();
    options.RegisterActivity<RequestClearanceActivity>();
    options.RegisterActivity<CheckWeatherActivity>();
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Dapr plumbing
app.UseCloudEvents();
app.MapSubscribeHandler();
app.MapActorsHandlers();

// --- Pub/sub subscriber: clearance results -> raise workflow event ---------
app.MapPost("/flight-ops/clearance-results", async (
    ClearanceResult result,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    var eventName = result.Kind switch
    {
        ClearanceKind.Takeoff => "clearance-takeoff",
        ClearanceKind.Landing => "clearance-landing",
        _ => "clearance-unknown",
    };

    logger.LogInformation(
        "Routing clearance result for {Callsign} ({Kind}, granted={Granted}) -> workflow {FlightId} event {Event}",
        result.Callsign, result.Kind, result.Granted, result.FlightId, eventName);

    await workflows.RaiseEventAsync(result.FlightId, eventName, result);
    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.ClearanceResults);

// --- Flights HTTP API ------------------------------------------------------
var flights = app.MapGroup("/flights");

flights.MapPost("/", async (
    ScheduleFlightRequest request,
    DaprClient dapr,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    var view = await FlightOps.ScheduleFlight(request, dapr, workflows, logger);
    return Results.Ok(view);
});

flights.MapGet("/", async (DaprClient dapr, ILogger<Program> logger) =>
{
    var ids = await dapr.GetStateAsync<List<string>>(DaprTopics.StateStoreName, FlightOps.IndexKey)
              ?? new List<string>();

    var views = new List<FlightView>(ids.Count);
    foreach (var id in ids)
    {
        try
        {
            var actor = AircraftActorProxy.For(id);
            var state = await actor.GetStateAsync();
            views.Add(FlightOps.ViewFromState(state));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read state for flight {FlightId}", id);
        }
    }

    var ordered = views
        .OrderBy(v => v.Status is FlightStatus.Landed or FlightStatus.Cancelled ? 1 : 0)
        .ThenBy(v => v.ScheduledDeparture)
        .ToArray();

    return Results.Ok(ordered);
});

flights.MapGet("/{flightId}", async (string flightId) =>
{
    var actor = AircraftActorProxy.For(flightId);
    var state = await actor.GetStateAsync();
    return Results.Ok(FlightOps.ViewFromState(state));
});

flights.MapPost("/seed", async (
    DaprClient dapr,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    var samples = new[]
    {
        new ScheduleFlightRequest("KL1611", "AMS", "BCN", "Boeing 737-800", "D12", DateTimeOffset.UtcNow.AddMinutes(5)),
        new ScheduleFlightRequest("BA438",  "AMS", "LHR", "Airbus A320",    "C7",  DateTimeOffset.UtcNow.AddMinutes(8)),
        new ScheduleFlightRequest("LH992",  "AMS", "FRA", "Airbus A321neo", "E22", DateTimeOffset.UtcNow.AddMinutes(12)),
        new ScheduleFlightRequest("AF1241", "AMS", "CDG", "Boeing 777-300", "F4",  DateTimeOffset.UtcNow.AddMinutes(15)),
    };

    var scheduled = new List<FlightView>(samples.Length);
    foreach (var s in samples)
    {
        scheduled.Add(await FlightOps.ScheduleFlight(s, dapr, workflows, logger));
    }
    return Results.Ok(scheduled);
});

app.Run();

// ---------------------------------------------------------------------------

internal static class FlightOps
{
    public const string IndexKey = "aircraft-index";

    public static async Task<FlightView> ScheduleFlight(
        ScheduleFlightRequest request,
        DaprClient dapr,
        DaprWorkflowClient workflows,
        ILogger logger)
    {
        var flightId = $"FL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var workflowInput = new FlightWorkflowInput(
            flightId, request.Callsign, request.Origin, request.Destination,
            request.AircraftType, request.Gate);

        // instanceId == flightId so clearance-result events can route back without an extra lookup.
        await workflows.ScheduleNewWorkflowAsync(
            name: nameof(FlightWorkflow),
            instanceId: flightId,
            input: workflowInput);

        var ids = await dapr.GetStateAsync<List<string>>(DaprTopics.StateStoreName, IndexKey)
                  ?? new List<string>();
        if (!ids.Contains(flightId))
        {
            ids.Add(flightId);
            await dapr.SaveStateAsync(DaprTopics.StateStoreName, IndexKey, ids);
        }

        logger.LogInformation("Scheduled flight {FlightId} ({Callsign}) {Origin}->{Destination}",
            flightId, request.Callsign, request.Origin, request.Destination);

        return new FlightView(
            FlightId: flightId,
            Callsign: request.Callsign,
            Origin: request.Origin,
            Destination: request.Destination,
            AircraftType: request.AircraftType,
            ScheduledDeparture: request.ScheduledDeparture,
            ActualDeparture: null,
            ActualArrival: null,
            Status: FlightStatus.Scheduled,
            Gate: request.Gate,
            Runway: null,
            Note: null);
    }

    public static FlightView ViewFromState(AircraftState s) => new(
        FlightId: s.FlightId,
        Callsign: s.Callsign,
        Origin: s.Origin,
        Destination: s.Destination,
        AircraftType: s.AircraftType,
        ScheduledDeparture: s.ScheduledDeparture,
        ActualDeparture: s.ActualDeparture,
        ActualArrival: s.ActualArrival,
        Status: s.Status,
        Gate: s.Gate,
        Runway: s.Runway,
        Note: s.Note);
}
