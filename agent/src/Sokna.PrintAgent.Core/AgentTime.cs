using System.Diagnostics;

namespace Sokna.PrintAgent.Core;

/// <summary>
/// Separates wall-clock time from monotonic elapsed time. Wall-clock values are suitable for
/// diagnostics and persisted timestamps; retry/deadline decisions anchored to an authoritative
/// server timestamp must advance with MonotonicNow so a Windows clock correction cannot make
/// an already-observed lease suddenly older/newer.
/// </summary>
public interface IAgentTimeSource
{
    DateTimeOffset UtcNow { get; }
    TimeSpan MonotonicNow { get; }
}

public sealed class SystemAgentTimeSource : IAgentTimeSource
{
    private readonly long _origin=Stopwatch.GetTimestamp();
    public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;
    public TimeSpan MonotonicNow=>Stopwatch.GetElapsedTime(_origin);
}

/// <summary>
/// Monotonic projection of an explicitly-offset server timestamp. It is intentionally small:
/// it does not claim NTP-grade synchronization and must not be used to infer server authority.
/// It only prevents host wall-clock jumps from changing elapsed-time decisions after an
/// authoritative server timestamp has been observed.
/// </summary>
public sealed class ServerTimeAnchor
{
    private readonly IAgentTimeSource _clock;
    private DateTimeOffset _serverAtAnchor;
    private TimeSpan _monotonicAtAnchor;
    private bool _hasObservation;

    public ServerTimeAnchor(IAgentTimeSource clock)
    {
        _clock=clock??throw new ArgumentNullException(nameof(clock));
    }

    public bool HasObservation=>_hasObservation;

    public void Observe(string serverTime)
    {
        _serverAtAnchor=ApiTimestampPolicy.ParseRequired(serverTime,"server_time");
        _monotonicAtAnchor=_clock.MonotonicNow;
        _hasObservation=true;
    }

    public DateTimeOffset EstimatedServerNow
    {
        get
        {
            if(!_hasObservation)throw new InvalidOperationException("No authoritative server timestamp has been observed.");
            var elapsed=_clock.MonotonicNow-_monotonicAtAnchor;
            if(elapsed<TimeSpan.Zero)throw new InvalidOperationException("Monotonic clock moved backwards.");
            return _serverAtAnchor+elapsed;
        }
    }

    public bool IsPast(DateTimeOffset authoritativeDeadline)
        =>EstimatedServerNow>=authoritativeDeadline;
}
