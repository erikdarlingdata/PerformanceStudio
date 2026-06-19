using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PlanViewer.Web;
using PlanViewer.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// One HttpClient shared by the share service instead of one per share/delete/load call.
builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddScoped<IPlanShareService, PlanShareService>();

await builder.Build().RunAsync();
