# `/operations` — Schedule a flight

**File:** [src/Airport.Web/Pages/Operations.razor](../../src/Airport.Web/Pages/Operations.razor)

Form over the persistent 3D airport. Submitting it kicks off the full Dapr workflow + actor + pub/sub flow; the new aircraft appears when the shared feed refreshes.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| Edit callsign / origin / destination / aircraft / gate | Inputs | — (local form state) |
| Pre-fill with a random plausible flight | **Randomize** button | uses `FlightFaker.NewRandom` |
| Submit the flight | **Schedule flight** button | `POST /flights` |
| Follow the last scheduled aircraft and open its controls | **Follow aircraft** | navigates to `/flight/{flightId}` |
| Explore the airport while scheduling | Shared scene toolbar / **Explore airport** | [workspace controls](../README.md#immersive-workspace-all-routes), no backend mutations |

After a successful submit, the form auto-randomizes again so consecutive clicks aren't duplicates. The "Last scheduled flight" card shows the returned `FlightView` (with the new `FlightId`).

## Backend interactions

Scheduling calls **FlightOperations** (`POST /flights`) via `FlightsApi.ScheduleAsync` ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)), then refreshes the shared snapshot. The workspace additionally reads `GET /flights`, `GET /weather/status`, `GET /clearances`, and `GET /atc/weather`; see the [shared feed table](../README.md#immersive-workspace-all-routes).

## What happens server-side

The page itself documents this on the "What happens when I click Schedule?" card. Mapped to code:

1. `POST /flights` → `FlightOps.ScheduleFlight` in [services/Airport.FlightOperations/Program.cs](../../src/services/Airport.FlightOperations/Program.cs).
2. Workflow started: `workflows.ScheduleNewWorkflowAsync(nameof(FlightWorkflow), instanceId: flightId, input)` — `instanceId == flightId` so clearance results route back without a lookup.
3. Flight id added to the `aircraft-index` list in `statestore`.
4. The `FlightWorkflow` ([Workflows/FlightWorkflow.cs](../../src/services/Airport.FlightOperations/Workflows/FlightWorkflow.cs)) then runs through:
   - `InitializeAircraftActivity` → creates the Aircraft actor.
   - `TryAcquireGateActivity` → Dapr distributed lock on `gate:<gate>`.
   - `CheckWeatherActivity` → reads FlightOperations' `latest-weather` snapshot from the Dapr state store.
   - If no event has arrived or weather is below minima, waits for a `weather-updated` workflow event, an operator advance, or the 60 s fallback recheck.
   - `RequestClearanceActivity` → publishes on `clearance-requests`.
   - Waits for the external `clearance-takeoff` event raised when ATC publishes the result.
   - Same loop for landing.

## Dapr building blocks touched

- **Workflow** (orchestration of the full lifecycle).
- **Actors** (`AircraftActor` is the per-flight state owner).
- **State store** (flight index + actor state + `latest-weather`).
- **Pub/sub** (`weather-updates` consumed by FlightOperations; `clearance-requests` published).
- **Workflow external events** (`weather-updated` wakes weather holds without shortening boarding/cruise).
- **Distributed lock** (gate occupancy).

## Telemetry

`FlightsScheduled` counter is incremented (tagged with `destination`). A `flight.schedule` span is opened with the deterministic per-flight parent context (`FlightTrace.ContextFor(flightId)`), so every later span for this flight lands in the same trace.

Weather published before scheduling is retained and available to the new flight. The server-side `POST /flight-ops/weather-updates` subscriber persists newer snapshots and signals waiting workflows; this is a Dapr delivery endpoint, not a browser call. An unknown-weather or bad-weather hold cancels after four retry waits (60 s or operator advance), releasing its gate. Weather notifications recheck immediately without consuming retries.
