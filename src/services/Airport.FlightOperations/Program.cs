using System.Diagnostics;
using Airport.Contracts;
using Airport.FlightOperations;
using Airport.FlightOperations.Aircraft;
using Airport.FlightOperations.Weather;
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
builder.Services.AddSingleton<FlightStateGate>();
builder.Services.AddSingleton<AirportStateReset>();
builder.Services.AddSingleton<WeatherSnapshotStore>();
builder.Services.AddTransient<WeatherUpdatesHandler>();

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
    options.RegisterWorkflow<AirportResetWorkflow>();
    options.RegisterActivity<PublishAirportResetActivity>();
    options.RegisterActivity<InitializeAircraftActivity>();
    options.RegisterActivity<UpdateAircraftStatusActivity>();
    options.RegisterActivity<RequestClearanceActivity>();
    options.RegisterActivity<CheckWeatherActivity>();
    options.RegisterActivity<TryAcquireGateActivity>();
    options.RegisterActivity<ReleaseGateActivity>();
    options.RegisterActivity<RecordGateWaitMetricActivity>();
    options.RegisterActivity<RecordFlightOutcomeMetricActivity>();
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

app.MapPost("/flight-ops/weather-updates", async (
    WeatherSnapshot snapshot, WeatherUpdatesHandler handler, FlightStateGate gate,
    CancellationToken cancellationToken) =>
{
    using var lease = await gate.EnterAsync(cancellationToken);
    await handler.HandleAsync(snapshot, cancellationToken);
    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.WeatherUpdates);

// --- Pub/sub subscriber: clearance results -> raise workflow event ---------
app.MapPost("/flight-ops/clearance-results", async (
    ClearanceResult result,
    DaprWorkflowClient workflows,
    FlightStateGate gate,
    ILogger<Program> logger) =>
{
    using var lease = await gate.EnterAsync();
    if (!await gate.ContainsAsync(result.FlightId))
        return Results.Ok();
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

// This subscriber must not take the flight gate: reset holds it while awaiting ATC's event.
app.MapPost("/flight-ops/airport-reset-completed", async (
    AirportResetCompleted completed, AirportStateReset reset, CancellationToken cancellationToken) =>
{
    await reset.AcknowledgeAsync(completed, cancellationToken);
    return Results.Ok();
})
.WithTopic(DaprTopics.PubSubName, DaprTopics.AirportResetCompleted);

// --- Flights HTTP API ------------------------------------------------------
var flights = app.MapGroup("/flights");
flights.AddEndpointFilter(async (context, next) =>
{
    var gate = context.HttpContext.RequestServices.GetRequiredService<FlightStateGate>();
    using var lease = await gate.EnterAsync(context.HttpContext.RequestAborted);
    if (context.HttpContext.Request.RouteValues["flightId"] is string flightId &&
        !await gate.ContainsAsync(flightId, context.HttpContext.RequestAborted))
        return Results.NotFound();
    return await next(context);
});

flights.MapDelete("/", async (
    AirportStateReset reset, IHostApplicationLifetime lifetime, ILogger<Program> logger) =>
{
    // Finish cleanup even if the browser disconnects, but never wait indefinitely on Dapr.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
    timeout.CancelAfter(TimeSpan.FromSeconds(90));
    try
    {
        await reset.ClearAsync(timeout.Token);
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Airport reset did not finish; retry to complete remaining cleanup");
        return Results.Problem(
            title: "Airport reset did not finish",
            detail: "Some state may already have been removed. Retry Clear airport state to finish cleanup.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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
    var samples = FlightFaker.NewRandomBatch(count: 4, now: DateTimeOffset.UtcNow);

    var scheduled = new List<FlightView>(samples.Count);
    foreach (var s in samples)
    {
        scheduled.Add(await FlightOps.ScheduleFlight(s, dapr, workflows, logger));
    }
    return Results.Ok(scheduled);
});

// --- Operator-only control endpoints ---------------------------------------

// Short-circuit whatever timer the workflow is currently sitting on (boarding hold,
// weather hold, cruise legs, clearance backoff after a denial). No effect if the
// workflow is waiting on a real external event (e.g. a clearance result) - use the
// /clearance endpoint for that case.
flights.MapPost("/{flightId}/advance", async (
    string flightId,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Operator advance requested for {FlightId}", flightId);
    await workflows.RaiseEventAsync(flightId, FlightWorkflow.AdvanceEventName, true);
    return Results.Ok();
});

// Terminate the workflow and mark the aircraft as Cancelled so the UI updates immediately.
// Also releases the gate lock so the next flight assigned to that gate can board.
flights.MapPost("/{flightId}/cancel", async (
    string flightId,
    DaprClient dapr,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Operator cancel requested for {FlightId}", flightId);

    string? gate = null;
    try
    {
        var actor = AircraftActorProxy.For(flightId);
        var state = await actor.GetStateAsync();
        gate = state.Gate;
        await actor.UpdateStatusAsync(new StatusUpdate(
            FlightStatus.Cancelled, Note: "Cancelled by operator"));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to update actor state during cancel of {FlightId}", flightId);
    }

    await workflows.TerminateWorkflowAsync(flightId, output: "cancelled by operator");

    AirportTelemetry.FlightsCancelled.Add(1,
        new KeyValuePair<string, object?>("reason", "operator"));

    if (!string.IsNullOrEmpty(gate))
    {
#pragma warning disable DAPR_DISTRIBUTEDLOCK
        try
        {
            await dapr.Unlock(
                storeName: DaprTopics.LockStoreName,
                resourceId: $"gate:{gate}",
                lockOwner: flightId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Gate {Gate} was not held by flight {FlightId} at cancel time", gate, flightId);
        }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
    }

    return Results.Ok();
});

// Force-grant or force-deny the currently-pending clearance, bypassing ATC. Raises the
// same external event ATC would raise via the clearance-results pub/sub topic.
flights.MapPost("/{flightId}/clearance", async (
    string flightId,
    ForceClearanceRequest body,
    DaprWorkflowClient workflows,
    ILogger<Program> logger) =>
{
    var eventName = body.Kind switch
    {
        ClearanceKind.Takeoff => "clearance-takeoff",
        ClearanceKind.Landing => "clearance-landing",
        _ => "clearance-unknown",
    };

    var actor = AircraftActorProxy.For(flightId);
    var state = await actor.GetStateAsync();

    var result = new ClearanceResult(
        FlightId: flightId,
        Callsign: state.Callsign,
        Kind: body.Kind,
        Granted: body.Granted,
        Runway: body.Granted ? (body.Runway ?? "27R") : "-",
        Reason: body.Granted ? "Manual clearance by operator" : "Manually denied by operator",
        DecidedAt: DateTimeOffset.UtcNow);

    logger.LogInformation(
        "Operator clearance for {FlightId}: {Kind} granted={Granted}", flightId, body.Kind, body.Granted);

    await workflows.RaiseEventAsync(flightId, eventName, result);
    return Results.Ok(result);
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

        // Root span for everything emitted for this flight. The parent context is derived
        // deterministically from the FlightId (see FlightTrace) so every workflow activity
        // we run later lands in the same trace in the dashboard.
        using var span = AirportTelemetry.Source.StartActivity(
            "flight.schedule",
            ActivityKind.Producer,
            parentContext: FlightTrace.ContextFor(flightId));
        if (span is not null)
        {
            span.DisplayName = $"flight.schedule {request.Callsign} {request.Origin}→{request.Destination}";
            span.SetTag("flight.id", flightId);
            span.SetTag("flight.callsign", request.Callsign);
            span.SetTag("flight.origin", request.Origin);
            span.SetTag("flight.destination", request.Destination);
            span.SetTag("flight.gate", request.Gate);
            span.SetTag("flight.aircraft_type", request.AircraftType);
        }

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

        AirportTelemetry.FlightsScheduled.Add(1,
            new KeyValuePair<string, object?>("destination", request.Destination));

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
