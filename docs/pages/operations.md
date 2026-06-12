# `/operations` — Schedule a flight

**File:** [src/Airport.Web/Pages/Operations.razor](../../src/Airport.Web/Pages/Operations.razor)

Form to schedule a new flight. Submitting it kicks off the full Dapr workflow + actor + pub/sub flow.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| Edit callsign / origin / destination / aircraft / gate | Inputs | — (local form state) |
| Pre-fill with a random plausible flight | **Randomize** button | uses `FlightFaker.NewRandom` |
| Submit the flight | **Schedule flight** button | `POST /flights` |

After a successful submit, the form auto-randomizes again so consecutive clicks aren't duplicates. The "Last scheduled flight" card shows the returned `FlightView` (with the new `FlightId`).

## Backend interactions

Single call to **FlightOperations** (`POST /flights`) via `FlightsApi.ScheduleAsync` ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)).

## What happens server-side

The page itself documents this on the right-hand "What happens when I click Schedule?" card. Mapped to code:

1. `POST /flights` → `FlightOps.ScheduleFlight` in [services/Airport.FlightOperations/Program.cs](../../src/services/Airport.FlightOperations/Program.cs).
2. Workflow started: `workflows.ScheduleNewWorkflowAsync(nameof(FlightWorkflow), instanceId: flightId, input)` — `instanceId == flightId` so clearance results route back without a lookup.
3. Flight id added to the `aircraft-index` list in `statestore`.
4. The `FlightWorkflow` ([Workflows/FlightWorkflow.cs](../../src/services/Airport.FlightOperations/Workflows/FlightWorkflow.cs)) then runs through:
   - `InitializeAircraftActivity` → creates the Aircraft actor.
   - `TryAcquireGateActivity` → Dapr distributed lock on `gate:<gate>`.
   - `CheckWeatherActivity` → Dapr service invocation `GET weather-service/weather`.
   - `RequestClearanceActivity` → publishes on `clearance-requests`.
   - Waits for the external `clearance-takeoff` event raised when ATC publishes the result.
   - Same loop for landing.

## Dapr building blocks touched

- **Workflow** (orchestration of the full lifecycle).
- **Actors** (`AircraftActor` is the per-flight state owner).
- **State store** (flight index + actor state).
- **Service invocation** (weather check).
- **Pub/sub** (`clearance-requests` topic).
- **Distributed lock** (gate occupancy).

## Telemetry

`FlightsScheduled` counter is incremented (tagged with `destination`). A `flight.schedule` span is opened with the deterministic per-flight parent context (`FlightTrace.ContextFor(flightId)`), so every later span for this flight lands in the same trace.
