using Airport.Contracts;
using Airport.FlightOperations.Aircraft;
using Airport.FlightOperations.Workflows.Activities;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows;

/// <summary>Final outcome of a flight workflow run.</summary>
public sealed record FlightWorkflowResult(bool Completed, string Summary);

/// <summary>
/// Orchestrates the lifecycle of a single flight:
/// schedule -> boarding -> weather check (retry loop) -> takeoff clearance ->
/// departed -> cruise -> landing clearance -> landed.
/// </summary>
/// <remarks>
/// The workflow itself does NO I/O. Every external interaction is wrapped in an
/// activity so the run can be deterministically replayed by the Dapr workflow engine.
/// </remarks>
public sealed class FlightWorkflow : Workflow<FlightWorkflowInput, FlightWorkflowResult>
{
    public override async Task<FlightWorkflowResult> RunAsync(
        WorkflowContext context,
        FlightWorkflowInput input)
    {
        // --- 1. Schedule + initialize aircraft actor ----------------------
        await context.CallActivityAsync<bool>(
            nameof(InitializeAircraftActivity),
            new AircraftInitData(
                input.FlightId, input.Callsign, input.Origin, input.Destination,
                input.AircraftType, input.Gate,
                context.CurrentUtcDateTime));

        await SetStatus(context, input.FlightId, new StatusUpdate(FlightStatus.BoardingPushback));
        await context.CreateTimer(TimeSpan.FromSeconds(6));

        // --- 2. Weather check, with a retry loop --------------------------
        WeatherSnapshot weather;
        for (var attempt = 0; ; attempt++)
        {
            weather = await context.CallActivityAsync<WeatherSnapshot>(
                nameof(CheckWeatherActivity), new CheckWeatherInput());

            if (weather.IsFlyable) break;

            if (attempt >= 4)
            {
                await SetStatus(context, input.FlightId, new StatusUpdate(
                    FlightStatus.Cancelled, Note: $"Cancelled: persistent bad weather ({weather.Condition})"));
                return new FlightWorkflowResult(false, "Cancelled: weather");
            }

            await SetStatus(context, input.FlightId, new StatusUpdate(
                FlightStatus.BoardingPushback, Note: $"Weather hold: {weather.Condition}"));
            await context.CreateTimer(TimeSpan.FromSeconds(6));
        }

        // --- 3. Takeoff clearance request/wait loop -----------------------
        var takeoff = await RequestClearanceWithRetry(
            context, input.FlightId, input.Callsign, ClearanceKind.Takeoff, eventName: "clearance-takeoff");

        if (takeoff is null)
        {
            await SetStatus(context, input.FlightId, new StatusUpdate(
                FlightStatus.Cancelled, Note: "Takeoff clearance denied repeatedly"));
            return new FlightWorkflowResult(false, "Cancelled: no takeoff clearance");
        }

        await SetStatus(context, input.FlightId, new StatusUpdate(
            FlightStatus.Departed,
            Runway: takeoff.Runway,
            ActualDeparture: context.CurrentUtcDateTime));

        // --- 4. Cruise (compressed for demo) ------------------------------
        await context.CreateTimer(TimeSpan.FromSeconds(8));
        await SetStatus(context, input.FlightId, new StatusUpdate(FlightStatus.Cruising));
        await context.CreateTimer(TimeSpan.FromSeconds(14));

        // --- 5. Landing clearance request/wait loop -----------------------
        await SetStatus(context, input.FlightId, new StatusUpdate(FlightStatus.AwaitingLandingClearance));

        var landing = await RequestClearanceWithRetry(
            context, input.FlightId, input.Callsign, ClearanceKind.Landing, eventName: "clearance-landing");

        if (landing is null)
        {
            await SetStatus(context, input.FlightId, new StatusUpdate(
                FlightStatus.Cancelled, Note: "Diverted: no landing clearance"));
            return new FlightWorkflowResult(false, "Diverted: no landing clearance");
        }

        await SetStatus(context, input.FlightId, new StatusUpdate(
            FlightStatus.Landed,
            Runway: landing.Runway,
            ActualArrival: context.CurrentUtcDateTime));

        return new FlightWorkflowResult(true, $"Landed runway {landing.Runway}");
    }

    private static Task SetStatus(WorkflowContext context, string flightId, StatusUpdate update) =>
        context.CallActivityAsync<bool>(
            nameof(UpdateAircraftStatusActivity),
            new UpdateAircraftStatusActivity.Input(flightId, update));

    /// <summary>
    /// Asks ATC for clearance, waits for the <paramref name="eventName"/> external event,
    /// and retries up to a few times if denied. Returns null if we give up.
    /// </summary>
    private static async Task<ClearanceResult?> RequestClearanceWithRetry(
        WorkflowContext context,
        string flightId,
        string callsign,
        ClearanceKind kind,
        string eventName)
    {
        const int MaxAttempts = 4;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await context.CallActivityAsync<bool>(
                nameof(RequestClearanceActivity),
                new ClearanceRequest(flightId, callsign, kind, context.CurrentUtcDateTime));

            ClearanceResult result;
            try
            {
                result = await context.WaitForExternalEventAsync<ClearanceResult>(
                    eventName, TimeSpan.FromSeconds(45));
            }
            catch (TaskCanceledException)
            {
                // Timed out — try again
                continue;
            }

            if (result.Granted) return result;

            // Denied: short backoff before retrying.
            await context.CreateTimer(TimeSpan.FromSeconds(5));
        }
        return null;
    }
}
