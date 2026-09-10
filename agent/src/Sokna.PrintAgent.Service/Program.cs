using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

AgentPaths? paths=null;
try
{
    var builder=Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options=>options.ServiceName="Sokna Print Agent 6");
    paths=AgentPaths.Default();
    paths.EnsureDirectories();

    var preparation=await QueueDatabaseBootstrap.PrepareAsync(paths.DatabasePath);
    if(preparation.ReinitializedLegacyEmptyDatabase)
    {
        new AgentLog(paths.LogsPath).Warn("queue_db_legacy_empty_reinitialized",$"Legacy empty queue database was preserved as backup: {preparation.BackupPath}");
    }

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(sp=>new LocalQueueStore(paths.DatabasePath));
    builder.Services.AddSingleton<IPrinterHealthProvider,WindowsPrinterHealthProvider>();
    builder.Services.AddSingleton<IAgentTimeSource,SystemAgentTimeSource>();
    builder.Services.AddSingleton<PrinterHealthState>();
    builder.Services.AddSingleton<IPrinterHealthReader>(sp=>sp.GetRequiredService<PrinterHealthState>());
    builder.Services.AddSingleton(sp=>new AgentLog(paths.LogsPath));
    builder.Services.AddSingleton<PrintWakeSignal>();
    builder.Services.AddSingleton<BridgeRuntimeState>();
    builder.Services.AddSingleton<ReportDeliveryPolicy>();
    builder.Services.AddSingleton<ReportDispatcher>();
    builder.Services.AddSingleton<DurableMutationRequestStore>();
    builder.Services.AddSingleton<IWorkerProcessFactory,SystemWorkerProcessFactory>();
    builder.Services.AddSingleton<WorkerSupervisor>();
    builder.Services.AddSingleton<IPreviewExecutor,SystemPreviewExecutor>();
    builder.Services.AddSingleton(sp=>new PreviewScheduler(sp.GetRequiredService<IPreviewExecutor>()));
    // Configuration/token are intentionally NOT loaded during DI construction. A fresh installation must
    // start as a healthy-but-unconfigured Windows Service so the Control App can configure it afterwards.
    builder.Services.AddHostedService<PrinterDiscoveryService>();
    builder.Services.AddHostedService<PrintAgentService>();
    builder.Services.AddHostedService<LocalBridgeService>();
    builder.Services.AddHostedService<WorkerEvidenceJanitor>();
    await builder.Build().RunAsync();
}
catch(Exception e)
{
    try
    {
        paths??=AgentPaths.Default();
        paths.EnsureDirectories();
        var safe=SafeLogText.Sanitize($"{e.GetType().Name}: {e.Message}",900);
        var diagnostic=new
        {
            timestamp_utc=DateTimeOffset.UtcNow.ToString("O"),
            stage="service_startup",
            exception_type=e.GetType().FullName,
            message=safe,
            database_path=paths.DatabasePath
        };
        var json=JsonSerializer.Serialize(diagnostic,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(paths.LogsPath,"startup-fatal.json"),json);
        File.AppendAllText(Path.Combine(paths.LogsPath,$"agent-{DateTime.UtcNow:yyyyMMdd}.log"),$"{DateTimeOffset.UtcNow:O}\tERROR\tservice_startup\t{safe}{Environment.NewLine}");
    }
    catch
    {
        // Never hide the original startup exception because diagnostics could not be written.
    }
    throw;
}
