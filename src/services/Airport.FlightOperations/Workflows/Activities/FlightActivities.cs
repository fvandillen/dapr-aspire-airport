using System.Diagnostics;
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
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.initialize_aircraft",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(data.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"init aircraft {data.Callsign}";
            span.SetTag("flight.id", data.FlightId);
            span.SetTag("flight.callsign", data.Callsign);
            span.SetTag("flight.gate", data.Gate);
        }

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
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.update_status",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"status → {input.Update.Status}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.status", input.Update.Status.ToString());
            if (input.Update.Runway is not null) span.SetTag("flight.runway", input.Update.Runway);
            if (input.Update.Note is not null) span.SetTag("flight.note", input.Update.Note);
        }

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
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.request_clearance",
            ActivityKind.Producer,
            parentContext: FlightTrace.ContextFor(request.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"request {request.Kind} clearance ({request.Callsign})";
            span.SetTag("flight.id", request.FlightId);
            span.SetTag("flight.callsign", request.Callsign);
            span.SetTag("clearance.kind", request.Kind.ToString());
        }

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
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.check_weather",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(context.InstanceId));
        if (span is not null)
        {
            span.DisplayName = $"check weather for {context.InstanceId}";
            span.SetTag("flight.id", context.InstanceId);
        }

        // Modern (non-obsolete) service invocation: a routed HttpClient pointed at the sidecar.
        using var client = DaprClient.CreateInvokeHttpClient(appId: "weather-service");
        var snapshot = await client.GetFromJsonAsync<WeatherSnapshot>("/weather")
            ?? throw new InvalidOperationException("WeatherService returned no snapshot");

        span?.SetTag("weather.condition", snapshot.Condition);
        span?.SetTag("weather.flyable", snapshot.IsFlyable);
        span?.SetTag("weather.wind_kt", snapshot.WindKnots);

        logger.LogInformation("Weather check: {Condition}, flyable={Flyable}",
            snapshot.Condition, snapshot.IsFlyable);
        return snapshot;
    }
}

/// <summary>Input for the gate-lock activities. The workflow id is the lock owner so the
/// lock automatically releases if the workflow is terminated.</summary>
public sealed record GateLockInput(string FlightId, string Gate);

/// <summary>
/// Tries once to acquire the Dapr distributed lock for the given gate. Returns true on success,
/// false if some other flight currently holds it. The workflow loops on this until it succeeds.
/// </summary>
/// <remarks>
/// Uses the alpha Dapr distributed-lock API. The lock owner is the flight id (== workflow id),
/// and the expiry acts as a safety net in case the workflow crashes without releasing.
/// </remarks>
public sealed class TryAcquireGateActivity(DaprClient dapr, ILogger<TryAcquireGateActivity> logger)
    : WorkflowActivity<GateLockInput, bool>
{
    // The distributed-lock API is still flagged Experimental in the Dapr .NET SDK.
#pragma warning disable DAPR_DISTRIBUTEDLOCK
    public override async Task<bool> RunAsync(WorkflowActivityContext context, GateLockInput input)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.try_acquire_gate",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"try acquire gate {input.Gate}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.gate", input.Gate);
        }

        var resourceId = $"gate:{input.Gate}";
        // 5 minutes is generous enough for boarding + pushback + a buffer; the workflow
        // unlocks explicitly the moment it transitions to Departed.
        var response = await dapr.Lock(
            storeName: DaprTopics.LockStoreName,
            resourceId: resourceId,
            lockOwner: input.FlightId,
            expiryInSeconds: 300);

        span?.SetTag("gate.lock_acquired", response.Success);
        if (response.Success)
        {
            logger.LogInformation("Acquired gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        else
        {
            logger.LogDebug("Gate {Gate} busy; flight {FlightId} waiting", input.Gate, input.FlightId);
        }
        return response.Success;
    }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
}

/// <summary>Releases the gate lock held by the flight. Safe to call even if no lock is held.</summary>
public sealed class ReleaseGateActivity(DaprClient dapr, ILogger<ReleaseGateActivity> logger)
    : WorkflowActivity<GateLockInput, bool>
{
#pragma warning disable DAPR_DISTRIBUTEDLOCK
    public override async Task<bool> RunAsync(WorkflowActivityContext context, GateLockInput input)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.release_gate",
            ActivityKind.Client,
            parentContext: FlightTrace.ContextFor(input.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"release gate {input.Gate}";
            span.SetTag("flight.id", input.FlightId);
            span.SetTag("flight.gate", input.Gate);
        }

        var resourceId = $"gate:{input.Gate}";
        try
        {
            var response = await dapr.Unlock(
                storeName: DaprTopics.LockStoreName,
                resourceId: resourceId,
                lockOwner: input.FlightId);
            logger.LogInformation("Released gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            // Best-effort: the lock will expire on its own.
            logger.LogWarning(ex, "Failed to release gate lock {Gate} for flight {FlightId}",
                input.Gate, input.FlightId);
        }
        return true;
    }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
}

/// <summary>Workflow -> metrics: how long the workflow had to wait on the gate distributed lock.</summary>
public sealed record GateWaitMetric(string FlightId, string Gate, double Seconds);

/// <summary>Records the <c>airport.flights.gate_wait_seconds</c> histogram.</summary>
public sealed class RecordGateWaitMetricActivity : WorkflowActivity<GateWaitMetric, bool>
{
    public override Task<bool> RunAsync(WorkflowActivityContext context, GateWaitMetric metric)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.record_gate_wait",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(metric.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"record gate-wait {metric.Seconds:0.0}s ({metric.Gate})";
            span.SetTag("flight.id", metric.FlightId);
            span.SetTag("flight.gate", metric.Gate);
            span.SetTag("gate.wait_seconds", metric.Seconds);
        }

        AirportTelemetry.GateWaitSeconds.Record(metric.Seconds,
            new KeyValuePair<string, object?>("gate", metric.Gate));
        return Task.FromResult(true);
    }
}

/// <summary>Workflow -> metrics: terminal flight outcome (Landed / Cancelled) and total wall-clock duration.</summary>
public sealed record FlightOutcomeMetric(string FlightId, FlightStatus Outcome, string? Reason, double DurationSeconds);

/// <summary>Records flight-lifetime metrics once when a workflow reaches a terminal state.</summary>
public sealed class RecordFlightOutcomeMetricActivity : WorkflowActivity<FlightOutcomeMetric, bool>
{
    public override Task<bool> RunAsync(WorkflowActivityContext context, FlightOutcomeMetric metric)
    {
        using var span = AirportTelemetry.Source.StartActivity(
            "workflow.activity.record_outcome",
            ActivityKind.Internal,
            parentContext: FlightTrace.ContextFor(metric.FlightId));
        if (span is not null)
        {
            span.DisplayName = $"outcome: {metric.Outcome} ({metric.DurationSeconds:0.0}s)";
            span.SetTag("flight.id", metric.FlightId);
            span.SetTag("flight.outcome", metric.Outcome.ToString());
            if (metric.Reason is not null) span.SetTag("flight.cancel_reason", metric.Reason);
            span.SetTag("flight.duration_seconds", metric.DurationSeconds);
        }

        AirportTelemetry.FlightDurationSeconds.Record(metric.DurationSeconds,
            new KeyValuePair<string, object?>("outcome", metric.Outcome.ToString()));

        if (metric.Outcome is FlightStatus.Landed)
        {
            AirportTelemetry.FlightsLanded.Add(1);
        }
        else if (metric.Outcome is FlightStatus.Cancelled)
        {
            AirportTelemetry.FlightsCancelled.Add(1,
                new KeyValuePair<string, object?>("reason", metric.Reason ?? "unknown"));
        }
        return Task.FromResult(true);
    }
}
