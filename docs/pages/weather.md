# `/weather` — Weather control panel

**File:** [src/Airport.Web/Pages/Weather.razor](../../src/Airport.Web/Pages/Weather.razor)

Shows the current weather snapshot and lets the operator steer it. Useful for demoing the impact of bad weather on ATC clearances.

## What the user can do

| Action | UI element | Calls |
| --- | --- | --- |
| See current condition / temperature / wind / visibility / cloud base / flyable badge | Auto-loaded cards (polls every 3 s) | `GET /weather/status` |
| Pause the periodic publisher (ATC stops getting fresh snapshots) | **Pause publishing** | `POST /weather/pause` |
| Resume publishing | **Resume publishing** | `POST /weather/resume` |
| Pin the weather to a preset (CAVOK, light rain, gusty winds, fog, thunderstorms, snow) | Preset buttons | `POST /weather/preset/{name}` |
| Drop the override and let the random walk resume | **Clear override** | `DELETE /weather/override` |
| Refresh now | **Refresh** | `GET /weather/status` |

The active preset button is highlighted (Default variant) while every other preset stays as Outline.

## Backend interactions

All calls go to **WeatherService** (`http://localhost:5081`) via `WeatherApi` ([Services/ApiClients.cs](../../src/Airport.Web/Services/ApiClients.cs)).

| Endpoint | Server behavior |
| --- | --- |
| `GET /weather/status` | Returns `WeatherStatus(Current, Paused, OverrideActive, PresetName, PublishIntervalSeconds)`. |
| `POST /weather/pause` | Sets `WeatherState.Paused = true`; the background publisher then skips ticks. |
| `POST /weather/resume` | Unpauses and **immediately publishes** so subscribers don't keep seeing stale data. |
| `POST /weather/preset/{name}` | Looks up a preset from `WeatherPresets`, pins it as the override, publishes immediately. 404 if unknown. |
| `PUT /weather/override` | Pins a caller-supplied snapshot as the override. |
| `DELETE /weather/override` | Clears the override; the next random tick will reflect the change. |

## How publishing works

`WeatherPublisher` ([services/Airport.WeatherService/Program.cs](../../src/services/Airport.WeatherService/Program.cs)) is an `IHostedService` that ticks every **60 s** (`WeatherPublisher.PublishInterval`):

1. If `Paused` → skip.
2. If `Override` is set → republish the pinned snapshot with a refreshed `ObservedAt`.
3. Else → emit a gentle random snapshot.
4. Publish to topic `weather-updates` on pub/sub component `pubsub`.

`WeatherSnapshot.IsFlyable` returns `true` when `WindKnots < 35 && VisibilityMeters >= 1500 && CloudBaseFeet >= 300` — drive any of those out of range with a preset to make ATC start denying takeoffs/landings.

## Dapr building blocks touched

- **Pub/sub** — publishes `weather-updates` periodically and on every control call that changes the effective state (resume, preset, override).

## Telemetry

- Each publish opens a `weather.publish` span with tags for condition / flyable / wind / visibility / override-active.
- `WeatherPublished` counter increments per publish, tagged with `weather.condition` and `weather.flyable`.

## Demo tips

- "Thunderstorms" or "Fog" presets push weather below minima → ATC denies clearances → the workflow's clearance retry loop kicks in.
- Pausing while a preset is active is a great way to show a hung subscriber: ATC's "Last weather observed" card on `/atc` stops updating.
