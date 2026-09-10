using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

/// <summary>
/// Owns the potentially blocking Windows printer enumeration outside the print coordinator.
/// There is never more than one native discovery call in flight. A timed-out call is not
/// replaced by another call until the original one finishes, preventing unbounded thread/task
/// growth when a driver or spooler API is stuck.
/// </summary>
public sealed class PrinterDiscoveryService : BackgroundService
{
    public static readonly TimeSpan DiscoveryInterval=TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DiscoveryTimeout=TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FreshnessWindow=TimeSpan.FromSeconds(20);

    private readonly IPrinterHealthProvider _provider;
    private readonly PrinterHealthState _state;
    private readonly ILogger<PrinterDiscoveryService> _log;
    private Task<IReadOnlyList<PrinterQueueHealth>>? _inFlight;
    private bool _timeoutReported;

    public PrinterDiscoveryService(
        IPrinterHealthProvider provider,
        PrinterHealthState state,
        ILogger<PrinterDiscoveryService> log)
    {
        _provider=provider;
        _state=state;
        _log=log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            if(_inFlight is null)
            {
                _timeoutReported=false;
                _inFlight=Task.Run(_provider.GetQueues);
            }

            if(_inFlight.IsCompleted)
            {
                CompleteDiscovery(_inFlight);
                _inFlight=null;
                await DelayAsync(DiscoveryInterval,stoppingToken);
                continue;
            }

            var timeoutTask=Task.Delay(DiscoveryTimeout,stoppingToken);
            var completed=await Task.WhenAny(_inFlight,timeoutTask);
            if(stoppingToken.IsCancellationRequested)break;

            if(completed==_inFlight)
            {
                CompleteDiscovery(_inFlight);
                _inFlight=null;
                await DelayAsync(DiscoveryInterval,stoppingToken);
                continue;
            }

            if(!_timeoutReported)
            {
                _timeoutReported=true;
                _state.MarkFailure("printer_discovery_timeout");
                _log.LogWarning("Printer discovery exceeded the {TimeoutMs}ms budget; cached health will age naturally.",(long)DiscoveryTimeout.TotalMilliseconds);
            }

            // Keep observing the same native call. Do not fan out additional EnumPrinters calls.
            await DelayAsync(DiscoveryInterval,stoppingToken);
        }
    }

    private void CompleteDiscovery(Task<IReadOnlyList<PrinterQueueHealth>> task)
    {
        try
        {
            _state.MarkSuccess(task.GetAwaiter().GetResult());
        }
        catch(Exception e)
        {
            var safe=SafeLogText.Sanitize(e.Message,300);
            _state.MarkFailure(safe);
            _log.LogWarning("Printer discovery failed: {Type}: {Message}",e.GetType().Name,safe);
        }
    }

    private static async Task DelayAsync(TimeSpan delay,CancellationToken ct)
    {
        try{await Task.Delay(delay,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
    }
}
