using Airport.Web;
using Airport.Web.Services;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// BlazorBlueprint: registers portal manager, focus trap, toast service, etc.
builder.Services.AddBlazorBlueprintComponents();

// Backend service URLs are configured in wwwroot/appsettings.json so a deployer
// can rewrite them without rebuilding the WASM payload.
string ServiceUrl(string name, string fallback) =>
    builder.Configuration[$"Services:{name}"] ?? fallback;

builder.Services.AddHttpClient<WeatherApi>(c => c.BaseAddress = new Uri(ServiceUrl("Weather", "http://localhost:5081")));
builder.Services.AddHttpClient<AtcApi>(c => c.BaseAddress = new Uri(ServiceUrl("Atc", "http://localhost:5082")));
builder.Services.AddHttpClient<FlightsApi>(c => c.BaseAddress = new Uri(ServiceUrl("FlightOps", "http://localhost:5083")));
builder.Services.AddScoped<AirportLiveState>();

await builder.Build().RunAsync();
