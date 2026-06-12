# `/flight/{flightId}` — Flight detail + operator controls

**File:** [src/Airport.Web/Pages/Flight.razor](../../src/Airport.Web/Pages/Flight.razor)

Per-flight detail view with a workflow-progress timeline and demo-only controls to drive the workflow manually.

## What the user can do

| Action | UI element | Calls | When enabled |
| --- | --- | --- | --- |
| See flight info (gate / runway / times / status / note) | Info card | `GET /flights/{id}` (poll every 2 s) | Always |
| Skip the current wait (boarding / cruise / clearance backoff) | **Skip wait (advance)** | `POST /flights/{id}/advance` | When status is `WaitingForGate`, `BoardingPushback`, `Departed`, or `Cruising` |
| Force-grant or deny takeoff clearance | **Force grant T/O** / **Force deny T/O** | `POST /flights/{id}/clearance` | When status is `AwaitingTakeoffClearance` |
| Force-grant or deny landing clearance | **Force grant LDG** / **Force deny LDG** | `POST /flights/{id}/clearance` | When status is `AwaitingLandingClearance` |
| Cancel the flight | **Cancel flight** | `POST /flights/{id}/cancel` | Until landed/cancelled |
| Refresh now | **Refresh** | `GET /flights/{id}` | Always |
| Go back to the board | **Back to board** | client-side `/` navigation | Always |

## Backend interactions

All calls go to **FlightOperations** via `FlightsApi`.

| Endpoint | Server behavior |
| --- | --- |
| `GET /flights/{id}` | Reads Aircraft actor state, returns `FlightView`. |
| `POST /flights/{id}/advance` | Raises the `advance` external event on the workflow → the current `WaitOrAdvance` returns immediately. No-op if the workflow is waiting on a clearance event. |
| `POST /flights/{id}/clearance` | Raises `clearance-takeoff` or `clearance-landing` with a synthetic `ClearanceResult`, bypassing ATC entirely. |
| `POST /flights/{id}/cancel` | Marks the actor as `Cancelled`, terminates the workflow, **and explicitly unlocks the gate** so the next flight can board. |

## Workflow timeline

The 8-step `<ol class="timeline">` is driven entirely client-side by `StepIndexFor(flight.Status)`:

| Index | Status | Step |
| --- | --- | --- |
| 0 | `Scheduled` | Workflow scheduled, aircraft actor created |
| 1 | `WaitingForGate` | Acquiring the Dapr distributed lock for this gate |
| 2 | `BoardingPushback` | Boarding & pushback |
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

## Notes for demos

- Cancel is the right way to free a gate mid-boarding; otherwise the gate stays locked until the 5-minute lock expiry kicks in.
- Force-grant / Force-deny **do not** go through ATC, so they won't show up under "Active runway clearances" on the `/atc` page.
