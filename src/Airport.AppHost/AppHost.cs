using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

// Absolute path to our hand-written Dapr component YAML files.
// Dapr sidecars receive this via `--resources-path` so they load OUR components
// instead of whatever lives under ~/.dapr/components.
var daprComponentsPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "dapr", "components"));

// Generate a Dapr Configuration that exports tracing to the Aspire dashboard's
// OTLP receiver. We bake the URL into the YAML at startup because Dapr's
// Configuration spec (unlike Component metadata) does not support
// `{env:VAR}` substitution.
var tracingConfigPath = TryGenerateTracingConfig(builder);

DaprSidecarOptions SidecarFor(string appId) => new()
{
    AppId = appId,
    ResourcesPaths = [daprComponentsPath],
    Config = tracingConfigPath,
    LogLevel = "info",
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

// ---------------------------------------------------------------------------

static string? TryGenerateTracingConfig(IDistributedApplicationBuilder builder)
{
    // Aspire pins the dashboard's OTLP receiver URL in launchSettings.json
    // via ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL (older builds: DOTNET_DASHBOARD_*).
    var otlpUrl = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]
                  ?? builder.Configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"];
    if (string.IsNullOrWhiteSpace(otlpUrl) ||
        !Uri.TryCreate(otlpUrl, UriKind.Absolute, out var uri))
    {
        return null;
    }

    var endpoint = $"{uri.Host}:{uri.Port}";
    var isSecure = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

    var outDir = Path.Combine(builder.AppHostDirectory, "obj", "dapr");
    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, "tracing.yaml");

    File.WriteAllText(path, $"""
        apiVersion: dapr.io/v1alpha1
        kind: Configuration
        metadata:
          name: tracing
        spec:
          tracing:
            samplingRate: "1"
            otel:
              endpointAddress: "{endpoint}"
              isSecure: {isSecure}
              protocol: grpc
        """);

    return path;
}
