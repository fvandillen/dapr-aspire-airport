# Demo Time setup

Slides + code-highlight flow for the .NET Friday session **Dapr + Aspire: Distributed Systems Without the Headache**.

## Files

The talk is split into six [Demo Time](https://demotime.show) acts (one YAML per act) plus a single slide deck:

| # | Act file | Scenes | What it covers |
| --- | --- | --- | --- |
| 1 | [01-intro.yaml](01-intro.yaml) | 4 | Title, agenda, the pain, meet the airport |
| 2 | [02-dapr.yaml](02-dapr.yaml) | 3 | What Dapr is, the sidecar pattern, building blocks & components |
| 3 | [03-aspire.yaml](03-aspire.yaml) | 5 | Aspire, AppHost, ServiceDefaults, integrations, Aspire + Dapr together |
| 4 | [04-observability.yaml](04-observability.yaml) | 3 | Why it matters, OTel in Aspire, custom metrics with `Meter` |
| 5 | [05-building-blocks.yaml](05-building-blocks.yaml) | 4 | Pub/Sub, state, event-driven weather, bonus workflows + actors |
| 6 | [06-recap.yaml](06-recap.yaml) | 1 | Recap and Q&A |

- [slides/dapr-aspire-session.md](slides/dapr-aspire-session.md) — single 20-slide deck shared by all acts (each scene references it by slide number).

## Before the talk

1. Install the VS Code extension `eliostruyf.vscode-demo-time`.
2. Start the distributed app yourself in a separate terminal (needed for the live demos you'll drive manually):
   ```sh
   cd src && aspire run
   ```
3. Open the Demo Time view in VS Code — all six act files are picked up automatically.

## Running the talk

Start at act **1 — Intro**, scene 1 (**Title**) and advance one scene at a time. When you reach the end of an act, jump to the next act in the Demo Time view. Each scene opens the slide; some also open one or two source files and highlight the relevant block(s). Browser navigations, terminal commands, and any UI clicks are all done by you, live in the running app.

### Scenes that highlight code

| Act | Scene | File(s) highlighted |
| --- | --- | --- |
| 3 — Aspire | aspire-service-defaults | `Airport.ServiceDefaults/Extensions.cs` — OTel block |
| 3 — Aspire | aspire-dapr-together | `Airport.AppHost/AppHost.cs` — `SidecarFor` + service wiring |
| 4 — Observability | custom-metrics | `Airport.Contracts/AirportTelemetry.cs` — clearance counter + histogram |
| 5 — Building blocks | pubsub-demo | `Airport.WeatherService/WeatherEventPublisher.cs` + `Airport.AtcService/Program.cs` (subscriber) |
| 5 — Building blocks | state-demo | `Airport.AtcService/Program.cs` — `Get/SaveStateAsync` block |
| 5 — Building blocks | weather-events-demo | `WeatherUpdatesHandler.cs` notification + `CheckWeatherActivity.cs` durable weather read |
| 5 — Building blocks | workflows-bonus | `FlightWorkflow.cs` takeoff section + `AircraftActor.UpdateStatusAsync` |

Highlights are anchored to unique text snippets (`startPlaceholder` / `endPlaceholder`) so they don't drift through unrelated code edits. If you rename or restructure a highlighted block, update the matching placeholder in the relevant act file. To check that every placeholder across every act still resolves to exactly one occurrence:

```sh
ruby -ryaml -e '
errors = 0
Dir.glob(".demo/*.yaml").sort.each { |f|
  doc = YAML.load_file(f)
  doc["scenes"].each { |s| s["moves"].each { |m|
    next unless m["action"] == "highlight"
    src = File.read(m["path"])
    %w[startPlaceholder endPlaceholder].each { |k|
      v = m[k]; next if v.nil?
      n = src.scan(v).length
      errors += 1 if n != 1
      puts "#{n == 1 ? "ok" : "BAD(#{n})"}  #{File.basename(f)} #{s["id"]} #{File.basename(m["path"])} #{k}"
    }
  }}
}
exit(errors)'
```
