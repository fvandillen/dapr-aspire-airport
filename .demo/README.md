# Demo Time setup

Slides + code-highlight flow for the .NET Friday session **Dapr + Aspire: Distributed Systems Without the Headache**.

## Files

- [01-dapr-aspire-session.yaml](01-dapr-aspire-session.yaml) — act file (Demo Time v3), 20 scenes (one per slide). Some scenes also `open` + `highlight` the matching source files.
- [slides/dapr-aspire-session.md](slides/dapr-aspire-session.md) — 20-slide deck.

## Before the talk

1. Install the VS Code extension `eliostruyf.vscode-demo-time`.
2. Start the distributed app yourself in a separate terminal (needed for the live demos you'll drive manually):
   ```sh
   cd src && aspire run
   ```
3. Open the Demo Time view in VS Code and load [01-dapr-aspire-session.yaml](01-dapr-aspire-session.yaml).

## Running the talk

Click play on scene 1 (**Title**) and advance one scene at a time. Each scene opens the slide; some also open one or two source files and highlight the relevant block(s). Browser navigations, terminal commands, and any UI clicks are all done by you, live in the running app.

### Scenes that highlight code

| # | Scene | File(s) highlighted |
| --- | --- | --- |
| 10 | aspire-service-defaults | `Airport.ServiceDefaults/Extensions.cs` — OTel block |
| 12 | aspire-dapr-together | `Airport.AppHost/AppHost.cs` — `SidecarFor` + service wiring |
| 15 | custom-metrics | `Airport.Contracts/AirportTelemetry.cs` — clearance counter + histogram |
| 16 | pubsub-demo | `Airport.WeatherService/Program.cs` (publisher) + `Airport.AtcService/Program.cs` (subscriber) |
| 17 | state-demo | `Airport.AtcService/Program.cs` — `Get/SaveStateAsync` block |
| 18 | invocation-demo | `FlightActivities.cs` — `CheckWeatherActivity` invoke block |
| 19 | workflows-bonus | `FlightWorkflow.cs` takeoff section + `AircraftActor.UpdateStatusAsync` |

Highlights are anchored to unique text snippets (`startPlaceholder` / `endPlaceholder`) so they don't drift through unrelated code edits. If you rename or restructure a highlighted block, update the matching placeholder in [01-dapr-aspire-session.yaml](01-dapr-aspire-session.yaml). To check all placeholders still resolve:

```sh
ruby -ryaml -e '
doc = YAML.load_file(".demo/01-dapr-aspire-session.yaml")
doc["scenes"].each { |s| s["moves"].each { |m|
  next unless m["action"] == "highlight"
  src = File.read(m["path"])
  %w[startPlaceholder endPlaceholder].each { |k|
    v = m[k]; next if v.nil?
    n = src.scan(v).length
    puts "#{n == 1 ? "ok" : "BAD(#{n})"}  #{s["id"]} #{File.basename(m["path"])} #{k}"
  }
}}'
```
