# `/` — Departure board

**File:** [src/Airport.Web/Pages/Home.razor](../../src/Airport.Web/Pages/Home.razor)

Live flight board. Polls FlightOperations every 3 s and renders one row per flight.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| See all flights + status counts (total / in the air / cancelled) | Auto-loaded table + metric cards | `GET /flights` |
| Refresh immediately | **Refresh** button | `GET /flights` |
| Seed 4 random demo flights | **Seed sample flights** button | `POST /flights/seed` |
| Open a flight's detail page | Click a row | navigates to `/flight/{flightId}` |

## Backend interactions

All requests go to the **FlightOperations** service (`http://localhost:5083`) via the typed `FlightsApi` HTTP client ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)).

- `GET /flights` — fans out to each Aircraft actor and returns a `FlightView[]` (active flights first, then landed/cancelled by scheduled time).
- `POST /flights/seed` — generates 4 flights via `FlightFaker` and starts a workflow per flight.

## Dapr building blocks touched (server-side, per request)

- **Actors**: `GET /flights` reads each `AircraftActor` state via `AircraftActorProxy.For(id).GetStateAsync()`.
- **State store**: the flight index is read from `statestore` under key `aircraft-index` to know which actors exist.
- **Workflows**: seeding starts a `FlightWorkflow` per flight (`ScheduleNewWorkflowAsync`).

## UI state

- `_flights: List<FlightView>` — last snapshot from the board endpoint.
- `_poller` — 3 s timer; swallowed errors keep the UI alive while backends warm up.
- Status colors / labels come from local `StatusClass` / `StatusLabel` helpers; landed + cancelled rows get the `muted-row` class so they fade.
