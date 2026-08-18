using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var builder=Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o=>o.ServiceName="Sokna Print Agent 6");
var paths=AgentPaths.Default();paths.EnsureDirectories();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(sp=>new LocalQueueStore(paths.DatabasePath));
builder.Services.AddSingleton<IPrinterHealthProvider,WindowsPrinterHealthProvider>();
builder.Services.AddSingleton(sp=>new AgentLog(paths.LogsPath));
// Configuration/token are intentionally NOT loaded during DI construction. A fresh installation must
// start as a healthy-but-unconfigured Windows Service so the Control App can configure it afterwards.
builder.Services.AddHostedService<PrintAgentService>();
await builder.Build().RunAsync();
