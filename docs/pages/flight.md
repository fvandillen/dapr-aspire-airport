# `/flight/{flightId}` — Flight detail + operator controls

**File:** [src/Airport.Web/Pages/Flight.razor](../../src/Airport.Web/Pages/Flight.razor)

Per-flight panel with a workflow-progress timeline and demo-only controls. Opening a flight selects it in the persistent scene and follows its aircraft; camera presets return to manual exploration.

## What the user can do

| Action | UI element | Calls | When enabled |
| --- | --- | --- | --- |
| See flight info (gate / runway / times / status / note) | Info card + selected 3D aircraft | `GET /flights/{id}` on entry; shared `GET /flights` snapshot every 2 s | Always |
| Skip the current wait (boarding / weather hold / cruise / clearance backoff) | **Skip wait (advance)** | `POST /flights/{id}/advance` | When status is `WaitingForGate`, `BoardingPushback`, `Departed`, or `Cruising` |
| Force-grant or deny takeoff clearance | **Force grant T/O** / **Force deny T/O** | `POST /flights/{id}/clearance` | When status is `AwaitingTakeoffClearance` |
| Force-grant or deny landing clearance | **Force grant LDG** / **Force deny LDG** | `POST /flights/{id}/clearance` | When status is `AwaitingLandingClearance` |
| Cancel the flight | **Cancel flight** | `POST /flights/{id}/cancel` | Until landed/cancelled |
| Refresh now | **Refresh** | `GET /flights/{id}` | Always |
| Go back to the board | **Back to board** | client-side `/` navigation | Always |
| Change camera / lighting / quality, pause motion, or hide the panel | Shared workspace controls | local visual changes; [workspace controls](../README.md#immersive-workspace-all-routes) | 3D controls require WebGL |

## Backend interactions

All calls go to **FlightOperations** via `FlightsApi`.

| Endpoint | Server behavior |
| --- | --- |
| `GET /flights/{id}` | Reads Aircraft actor state, returns `FlightView`. |
| `GET /flights` | Shared snapshot updates flight information and all scene aircraft every 2 s. |
| `POST /flights/{id}/advance` | Raises `advance` to skip a timed wait or prompt a weather recheck. Does not bypass weather minima; during a weather hold it consumes one retry. No-op if the workflow is waiting on a clearance event. |
| `POST /flights/{id}/clearance` | Raises `clearance-takeoff` or `clearance-landing` with a synthetic `ClearanceResult`, bypassing ATC entirely. |
| `POST /flights/{id}/cancel` | Marks the actor as `Cancelled`, terminates the workflow, **and explicitly unlocks the gate** so the next flight can board. |

The persistent workspace also reads `GET /weather/status`, `GET /clearances`, and `GET /atc/weather`; see the [shared feed table](../README.md#immersive-workspace-all-routes). Successful commands refresh shared scene data. Failed reads or commands are shown in the panel rather than silently ignored.

## Workflow timeline

The 8-step `<ol class="timeline">` is driven entirely client-side by `StepIndexFor(flight.Status)`:

| Index | Status | Step |
| --- | --- | --- |
| 0 | `Scheduled` | Workflow scheduled, aircraft actor created |
| 1 | `WaitingForGate` | Acquiring the Dapr distributed lock for this gate |
| 2 | `BoardingPushback` | Boarding & pushback, then an optional weather hold shown in the note |
| 3 | `AwaitingTakeoffClearance` | Requesting takeoff clearance |
| 4 | `Departed` | Cleared, taxiing and rolling for takeoff |
| 5 | `Cruising` | En-route at cruise altitude |
| 6 | `AwaitingLandingClearance` | Requesting landing clearance |
| 7 | `Landed` | Touched down |

`Cancelled` freezes the marker at step 2 (visually highlighted as cancelled).

## Dapr building blocks touched (server-side)

- **Actors** — every poll reads `AircraftActor` state; cancel writes to it.
- **Workflows** — `advance`, `cancel`, and `clearance` all manipulate the workflow instance (`RaiseEventAsync` / `TerminateWorkflowAsync`).
- **Distributed lock** — cancel calls `dapr.Unlock("lockstore", "gate:<gate>", lockOwner: flightId)` to free the gate.
- **Pub/sub + state store** — FlightOperations' `POST /flight-ops/weather-updates` subscriber saves `latest-weather` and signals weather holds via `weather-updated`. This is not called by the browser.
- **Workflow weather gate** — `CheckWeatherActivity` reads that snapshot; missing/unflyable weather waits on an event, `advance`, or a 60 s fallback. Four retry waits without flyable weather cancel the flight and release its gate. Duplicate weather messages do not consume retries, and old messages cannot restore superseded conditions.

## Notes for demos

- Cancel is the right way to free a gate mid-boarding; otherwise the gate stays locked until the 5-minute lock expiry kicks in.
- Force-grant / Force-deny **do not** go through ATC, so they won't show up under "Active runway clearances" on the `/atc` page.
- Changing weather can wake a weather hold immediately, but does not skip the initial 60 s boarding period or a cruise wait.
- Workflows that already failed before the event-driven implementation must be cancelled/rescheduled; publishing new weather does not revive a terminal workflow.
