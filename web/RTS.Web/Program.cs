using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// No Razor components: the page is a canvas driven from wwwroot/game.js through the
// [JSExport] surface in GameApi. Blazor only hosts the .NET runtime for us.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
await builder.Build().RunAsync();
