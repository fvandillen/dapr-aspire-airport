# `/` — Live traffic + 3D airport

**File:** [src/Airport.Web/Pages/Home.razor](../../src/Airport.Web/Pages/Home.razor)

Live traffic panel over the persistent 3D airport. Uses the shared SignalR-driven snapshot and renders an accessible flight card per flight. Aircraft placement illustrates workflow state, not geographic telemetry.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| See all flights + status counts (total / active / landed / airborne / cancelled) | Flight cards, summary strip, 3D aircraft | `GET /flights` |
| Refresh immediately | **Refresh** button | `GET /flights` |
| Seed 4 random demo flights | **Seed sample flights** button | `POST /flights/seed` |
| Open a flight's controls and follow it in 3D | Flight card, aircraft, or aircraft label | navigates to `/flight/{flightId}` |
| Filter flights by callsign, destination, or gate | Search box | local filtering; scene still shows all flights |
| Schedule an individual flight | **New flight** | navigates to `/operations` |
| Explore viewpoints, lighting, quality, motion, and labels | Shared scene toolbar | local visual controls; see [workspace controls](../README.md#immersive-workspace-all-routes) |

## Backend interactions

Flight requests go to **FlightOperations** (`http://localhost:5083`) via the typed `FlightsApi` HTTP client ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)). The shared workspace also reads `GET /weather/status`, `GET /clearances`, and `GET /atc/weather` for its scene and summary strip; see the [shared feed table](../README.md#immersive-workspace-all-routes).

- `GET /flights` — fans out to each Aircraft actor and returns a `FlightView[]` (active flights first, then landed/cancelled by scheduled time).
- `POST /flights/seed` — generates 4 flights via `FlightFaker` and starts a workflow per flight.
- SignalR `/hubs/airport` — FlightOperations sends `Changed` after scheduling, actor state transitions and removal/reset. Each notification queues a fresh `GET /flights`; idle clients do not poll.

## Dapr building blocks touched (server-side, per request)

- **Actors**: `GET /flights` reads each `AircraftActor` state via `AircraftActorProxy.For(id).GetStateAsync()`.
- **State store**: the flight index is read from `statestore` under key `aircraft-index` to know which actors exist.
- **Workflows**: seeding starts a `FlightWorkflow` per flight (`ScheduleNewWorkflowAsync`).

## UI state

- `AirportLiveState` retains the last successful snapshots and surfaces connection failures; the board distinguishes connecting, empty, filtered-empty, and stale states.
- Search is local; seeding errors appear in the panel. Seeding never happens automatically.
- Landed and cancelled cards are visually subdued but remain selectable. The scene does not advance workflow state; the backend remains authoritative.
