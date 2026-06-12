using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

// Absolute path to our hand-written Dapr component YAML files.
// Dapr sidecars receive this via `--resources-path` so they load OUR components
// instead of whatever lives under ~/.dapr/components.
var daprComponentsPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "dapr", "components"));

DaprSidecarOptions SidecarFor(string appId) => new()
{
    AppId = appId,
    ResourcesPaths = [daprComponentsPath],
    LogLevel = "info"
};

// --- Backend services (each with its own Dapr sidecar) -----------------------

var weather = builder.AddProject<Projects.Airport_WeatherService>("weather-service")
    .WithHttpEndpoint(port: 5081, name: "http")
    .WithDaprSidecar(SidecarFor("weather-service"));

var atc = builder.AddProject<Projects.Airport_AtcService>("atc-service")
    .WithHttpEndpoint(port: 5082, name: "http")
    .WithDaprSidecar(SidecarFor("atc-service"));

var flightOps = builder.AddProject<Projects.Airport_FlightOperations>("flight-ops")
    .WithHttpEndpoint(port: 5083, name: "http")
    .WithDaprSidecar(SidecarFor("flight-ops"));

// --- Blazor WASM frontend (no Dapr sidecar; the browser cannot reach one) ---
//
// Service URLs are passed via Aspire references for `dotnet publish` scenarios,
// but the WASM itself reads the URLs from wwwroot/appsettings.json since
// service discovery doesn't work inside the browser sandbox.
builder.AddProject<Projects.Airport_Web>("web")
    .WithHttpEndpoint(port: 5080, name: "http")
    .WithReference(weather)
    .WithReference(atc)
    .WithReference(flightOps)
    .WaitFor(weather)
    .WaitFor(atc)
    .WaitFor(flightOps);

builder.Build().Run();
