using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

/// <summary>
/// Single in-process source of truth for printer discovery results. Wall-clock timestamps are
/// diagnostic only; freshness is derived from the monotonic clock so Windows clock corrections
/// cannot make a stale queue appear fresh again.
/// </summary>
public sealed class PrinterHealthState: IPrinterHealthReader
{
    private readonly object _gate=new();
    private readonly IAgentTimeSource _clock;
    private IReadOnlyList<PrinterQueueHealth> _queues=[];
    private DateTimeOffset? _lastSuccessAt;
    private TimeSpan? _lastSuccessMonotonic;
    private DateTimeOffset? _lastFailureAt;
    private string? _lastError;
    private long _generation;

    public PrinterHealthState(IAgentTimeSource clock)
    {
        _clock=clock??throw new ArgumentNullException(nameof(clock));
    }

    public void MarkSuccess(IReadOnlyList<PrinterQueueHealth> queues)
    {
        ArgumentNullException.ThrowIfNull(queues);
        var copy=queues.ToArray();
        lock(_gate)
        {
            _queues=copy;
            _lastSuccessAt=_clock.UtcNow;
            _lastSuccessMonotonic=_clock.MonotonicNow;
            _lastError=null;
            _generation++;
        }
    }

    public void MarkFailure(string error)
    {
        var safe=SafeLogText.Sanitize(error??"printer_discovery_failed",300);
        lock(_gate)
        {
            _lastFailureAt=_clock.UtcNow;
            _lastError=safe;
            _generation++;
        }
    }

    public PrinterHealthSnapshot Read(TimeSpan freshnessWindow)
    {
        if(freshnessWindow<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(freshnessWindow));
        lock(_gate)
        {
            long? ageMilliseconds=null;
            var fresh=false;
            if(_lastSuccessMonotonic is { } successMono)
            {
                var elapsed=_clock.MonotonicNow-successMono;
                if(elapsed<TimeSpan.Zero)elapsed=TimeSpan.MaxValue;
                ageMilliseconds=(long)Math.Min(long.MaxValue,Math.Max(0,elapsed.TotalMilliseconds));
                fresh=elapsed<=freshnessWindow;
            }
            return new PrinterHealthSnapshot(
                _queues.ToArray(),
                _lastSuccessAt,
                _lastFailureAt,
                _lastError,
                ageMilliseconds,
                fresh,
                _generation);
        }
    }
}
