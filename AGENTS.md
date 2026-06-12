# Dotnet friday session
This repository contains a presentation for .NET Friday. It uses Demo time to present easily, both slides and code demo.

## Session title 
Dapr + Aspire: Distributed Systems Without the Headache

## Session abstract
Dapr promises portable distributed application building blocks. Aspire promises a streamlined local developer experience for cloud-native applications. Combined, they form a powerful platform for building observable, event-driven microservices without drowning in infrastructure complexity.

In this session, we’ll start with a quick introduction to Aspire and set up a distributed application from scratch. From there, we’ll explore the core Dapr concepts: the sidecar architecture, building blocks, and components. You’ll see how Aspire makes it easy to enable Dapr on services and orchestrate everything locally with minimal setup.

Using practical demos, we’ll walk through Dapr Pub/Sub messaging, state management with a state store, and service observability using OpenTelemetry. Along the way, we’ll discuss how these patterns help teams build resilient, maintainable distributed systems while keeping developers productive.

By the end of the session, you’ll understand how Aspire and Dapr complement each other, how to get started quickly, and how to apply these patterns in real-world .NET applications.

## Demo app

The code demo is an "Airport" distributed system: a Blazor WASM frontend (`Airport.Web`) calling three Dapr-enabled backends (`Airport.WeatherService`, `Airport.AtcService`, `Airport.FlightOperations`) orchestrated by an Aspire AppHost. It exercises pub/sub, state store, distributed lock, actors, workflows, service invocation, and OpenTelemetry tracing across all of them.

**Before editing demo code, read the relevant docs first** — they are the source of truth for what each page does and which backend endpoints / Dapr building blocks it touches.

- [docs/README.md](docs/README.md) — system overview: topology, ports, Dapr components, end-to-end sequence diagram, page → doc index.
- Per-page functional docs (UI actions ↔ backend endpoints ↔ Dapr building blocks):
  - [docs/pages/home.md](docs/pages/home.md) — `/` departure board.
  - [docs/pages/operations.md](docs/pages/operations.md) — `/operations` schedule a flight (kicks off the full workflow).
  - [docs/pages/flight.md](docs/pages/flight.md) — `/flight/{id}` detail + operator controls (advance / force clearance / cancel).
  - [docs/pages/atc.md](docs/pages/atc.md) — `/atc` tower view (pub/sub subscriber, state-store reader).
  - [docs/pages/weather.md](docs/pages/weather.md) — `/weather` control panel (publisher pause / preset overrides).

### Doc maintenance rules

When you change demo code, keep these docs in sync:

- **Add or change a UI action / button** on a page → update that page's "What the user can do" table.
- **Add or change an HTTP endpoint** on a backend service → update the "Backend interactions" table on every page that calls it.
- **Touch a Dapr building block** (new pub/sub topic, new state key, new lock, new actor method, new workflow activity) → update the affected page's "Dapr building blocks touched" section, and [docs/README.md](docs/README.md) if the topology changes.
- **Add a new page or route** → create a new `docs/pages/<page>.md` following the existing skeleton (What the user can do → Backend interactions → Dapr building blocks touched → Telemetry / notes) and link it from the page → doc map in [docs/README.md](docs/README.md).
- **Change a port, app id, or Dapr component** → update the topology table and components list in [docs/README.md](docs/README.md).

Keep docs concise and functionality-focused — no change logs, no implementation walkthroughs beyond what's needed to understand what the page does.