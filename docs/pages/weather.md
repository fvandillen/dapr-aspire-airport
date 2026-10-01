# `/weather` — Weather control panel

**File:** [src/Airport.Web/Pages/Weather.razor](../../src/Airport.Web/Pages/Weather.razor)

Panel over the persistent airport that shows and controls current weather. Scene precipitation, fog, and surface appearance respond to this snapshot, making the impact of bad weather on ATC clearances visible.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| See current condition / temperature / wind / visibility / cloud base / flyable badge | Auto-loaded cards and 3D weather (shared refresh every 2 s) | `GET /weather/status` |
| Pause the periodic publisher (ATC and FlightOperations retain their last received snapshots) | **Pause publishing** | `POST /weather/pause` |
| Resume publishing | **Resume publishing** | `POST /weather/resume` |
| Pin the weather to a preset (CAVOK, light rain, gusty winds, fog, thunderstorms, snow) | Preset buttons | `POST /weather/preset/{name}` |
| Drop the override and let the random walk resume | **Clear override** | `DELETE /weather/override` |
| Refresh now | **Refresh** | `GET /weather/status` |
| Choose scene time of day / quality or pause visual motion | Shared scene toolbar | local visual settings only; does not change published weather |

The active preset button is highlighted (Default variant) while every other preset stays as Outline.

## Backend interactions

Weather calls go to **WeatherService** (`http://localhost:5081`) via `WeatherApi` ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)). The workspace also reads `GET /flights`, `GET /clearances`, and `GET /atc/weather`; see the [shared feed table](../README.md#immersive-workspace-all-routes). Failed reads or commands are shown in the panel, and successful commands refresh the scene's snapshot.

| Endpoint | Server behavior |
| --- | --- |
| `GET /weather/status` | Returns `WeatherStatus(Current, Paused, OverrideActive, PresetName, PublishIntervalSeconds)`. |
| `POST /weather/pause` | Sets `WeatherState.Paused = true`; the background publisher then skips ticks. |
| `POST /weather/resume` | Unpauses and **immediately publishes** so subscribers don't keep seeing stale data. |
| `POST /weather/preset/{name}` | Looks up a preset from `WeatherPresets`, pins it as the override, publishes immediately. 404 if unknown. |
| `PUT /weather/override` | Pins a caller-supplied snapshot with a fresh observation time and publishes immediately. |
| `DELETE /weather/override` | Clears the override; the next random tick will reflect the change. |

## How publishing works

`WeatherPublisher` ([services/Airport.WeatherService/Program.cs](../../src/services/Airport.WeatherService/Program.cs)) is an `IHostedService` that ticks every **60 s** (`WeatherPublisher.PublishInterval`):

1. If `Paused` → skip.
2. If `Override` is set → republish the pinned snapshot with a refreshed `ObservedAt`.
3. Else → emit a gentle random snapshot.
4. Publish to topic `weather-updates` on pub/sub component `pubsub`, consumed independently by ATC and FlightOperations.

Preset/override changes and resume publish immediately, even when periodic publishing was paused. `WeatherEventPublisher` is shared by control endpoints and the periodic loop, so both paths emit the same events and telemetry. A failed control publication returns an error to the UI; periodic failures are logged and retried on the next tick.

FlightOperations' `POST /flight-ops/weather-updates` subscriber persists the newest `ObservedAt` under its app-scoped `latest-weather` key, then signals running workflows with custom status `waiting-for-weather`. A signal prompts `CheckWeatherActivity` to read the durable latest snapshot rather than invoke WeatherService. Duplicates can retry notification after partial delivery, but do not replace the snapshot or spend retry attempts; older snapshots are ignored by both consumers.

`WeatherSnapshot.IsFlyable` returns `true` when `WindKnots < 35 && VisibilityMeters >= 1500 && CloudBaseFeet >= 300` — drive any of those out of range with a preset to make ATC start denying takeoffs/landings.

## Dapr building blocks touched

- **Pub/sub** — publishes `weather-updates` periodically and on resume, preset, and override.
- **State store** — FlightOperations saves `latest-weather` before acknowledging the event; flights started later and restarted services can read it.
- **Workflow external events** — `weather-updated` wakes weather holds immediately. Missing or unflyable weather remains a hold, with four timed/manual retries before cancellation and gate release.

## Telemetry

- Each publish opens a `weather.publish` span with tags for condition / flyable / wind / visibility / override-active.
- `WeatherPublished` counter increments per publish, tagged with `weather.condition` and `weather.flyable`.
- `flight-ops.weather_updated` spans consumption; `workflow.activity.check_weather` remains in the per-flight trace and now records a state-store read (including `weather.available`).

## Demo tips

- "Thunderstorms" or "Fog" before takeoff keep the workflow on a weather hold. Switch to CAVOK to wake it without pressing advance. ATC also applies the published weather when deciding clearances.
- Pausing while a preset is active is a great way to show a hung subscriber: ATC's "Last weather observed" card on `/atc` stops updating.
- Scene motion pause is independent of publisher pause: freezing animation does not stop backend workflows or live snapshot refreshes.
