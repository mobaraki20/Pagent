using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public enum PreviewScheduleStatus
{
    Completed,
    Busy,
    Superseded,
    Cancelled,
    Timeout,
    Failed
}

public sealed record PreviewWorkRequest(
    string SessionId,
    long Revision,
    string PayloadJson,
    double PaperWidthMm,
    double PrintableWidthMm,
    int DpiX,
    int DpiY,
    PreviewSafetyLimits SafetyLimits,
    TimeSpan ExecutionTimeout,
    TimeSpan ExitProofTimeout);

public sealed record PreviewRenderData(
    byte[] ImageBytes,
    int Width,
    int Height,
    int DpiX,
    int DpiY,
    string PngSha256,
    string RendererVersion,
    string FontFamily,
    bool BundledFont);

public sealed record PreviewScheduleResult(
    PreviewScheduleStatus Status,
    string Code,
    long Revision,
    string SessionId,
    PreviewRenderData? Render=null,
    string? Error=null)
{
    public bool Success=>Status==PreviewScheduleStatus.Completed&&Render is not null;

    public static PreviewScheduleResult Busy(PreviewWorkRequest request)
        =>new(PreviewScheduleStatus.Busy,"preview_busy",request.Revision,request.SessionId);
    public static PreviewScheduleResult Superseded(PreviewWorkRequest request)
        =>new(PreviewScheduleStatus.Superseded,"preview_superseded",request.Revision,request.SessionId);
    public static PreviewScheduleResult Cancelled(PreviewWorkRequest request)
        =>new(PreviewScheduleStatus.Cancelled,"preview_cancelled",request.Revision,request.SessionId);
    public static PreviewScheduleResult Timeout(PreviewWorkRequest request)
        =>new(PreviewScheduleStatus.Timeout,"preview_timeout",request.Revision,request.SessionId);
    public static PreviewScheduleResult Failed(PreviewWorkRequest request,string code,string? error=null)
        =>new(PreviewScheduleStatus.Failed,code,request.Revision,request.SessionId,null,error);
    public static PreviewScheduleResult Completed(PreviewWorkRequest request,PreviewRenderData render)
        =>new(PreviewScheduleStatus.Completed,"ok",request.Revision,request.SessionId,render);
}

public interface IPreviewExecutor
{
    Task<PreviewScheduleResult> ExecuteAsync(PreviewWorkRequest request,CancellationToken ct);
}

public sealed record PreviewSchedulerSnapshot(
    int ActiveCount,
    int PendingCount,
    int SessionCount,
    int MaxPendingGlobal,
    long Accepted,
    long BusyRejected,
    long Superseded);

/// <summary>
/// R07 owner for preview concurrency. Exactly one preview renderer may be active globally;
/// each session may own at most one queued revision; newest-wins applies only inside that
/// session. The global waiting set is bounded, so HTTP pressure cannot become an unbounded
/// in-memory or child-process queue. Preview never owns print state or a print attempt.
/// </summary>
public sealed class PreviewScheduler : IAsyncDisposable
{
    private readonly object _gate=new();
    private readonly IPreviewExecutor _executor;
    private readonly Dictionary<string,SessionState> _sessions=new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pendingSessions=new();
    private readonly CancellationTokenSource _shutdown=new();
    private int _maxPendingGlobal;
    private TimeSpan _revisionTtl;
    private ActiveWork? _active;
    private Task? _pump;
    private bool _disposed;
    private long _accepted;
    private long _busyRejected;
    private long _superseded;

    public PreviewScheduler(IPreviewExecutor executor,int maxPendingGlobal=4,TimeSpan? revisionTtl=null)
    {
        _executor=executor??throw new ArgumentNullException(nameof(executor));
        _maxPendingGlobal=ValidateMaxPending(maxPendingGlobal);
        _revisionTtl=ValidateTtl(revisionTtl??TimeSpan.FromMinutes(10));
    }

    public void Configure(int maxPendingGlobal,TimeSpan revisionTtl)
    {
        lock(_gate)
        {
            ThrowIfDisposed();
            _maxPendingGlobal=ValidateMaxPending(maxPendingGlobal);
            _revisionTtl=ValidateTtl(revisionTtl);
            PruneSessionsNoLock(DateTimeOffset.UtcNow);
        }
    }

    public PreviewSchedulerSnapshot Snapshot
    {
        get
        {
            lock(_gate)
            {
                return new(
                    _active is null?0:1,
                    _pendingSessions.Count,
                    _sessions.Count,
                    _maxPendingGlobal,
                    _accepted,
                    _busyRejected,
                    _superseded);
            }
        }
    }

    public Task<PreviewScheduleResult> SubmitAsync(PreviewWorkRequest request,CancellationToken requestCancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.SessionId.Length is <8 or >96)throw new InvalidDataException("Preview session_id معتبر نیست.");
        if(request.Revision<1)throw new InvalidDataException("Preview revision معتبر نیست.");

        PendingWork? replaced=null;
        ActiveWork? activeToCancel=null;
        PendingWork pending;
        lock(_gate)
        {
            ThrowIfDisposed();
            var now=DateTimeOffset.UtcNow;
            PruneSessionsNoLock(now);
            if(!_sessions.TryGetValue(request.SessionId,out var session))
            {
                session=new SessionState();
                _sessions.Add(request.SessionId,session);
            }

            if(request.Revision<=session.LatestRevision)
            {
                _superseded++;
                return Task.FromResult(PreviewScheduleResult.Superseded(request));
            }

            if(session.Pending is null&&_pendingSessions.Count>=_maxPendingGlobal)
            {
                _busyRejected++;
                return Task.FromResult(PreviewScheduleResult.Busy(request));
            }

            session.LatestRevision=request.Revision;
            session.LastSeen=now;
            pending=new PendingWork(request,requestCancellation);
            if(session.Pending is not null)
            {
                replaced=session.Pending;
                session.Pending=pending;
            }
            else
            {
                session.Pending=pending;
                session.QueueNode=_pendingSessions.AddLast(request.SessionId);
            }

            if(_active is { } active&&string.Equals(active.Request.SessionId,request.SessionId,StringComparison.Ordinal)&&active.Request.Revision<request.Revision)
                activeToCancel=active;

            _accepted++;
            EnsurePumpNoLock();
        }

        if(replaced is not null)
        {
            Interlocked.Increment(ref _superseded);
            replaced.Completion.TrySetResult(PreviewScheduleResult.Superseded(replaced.Request));
        }
        activeToCancel?.Cancellation.Cancel();
        return pending.Completion.Task;
    }

    private void EnsurePumpNoLock()
    {
        if(_pump is null)_pump=Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while(true)
        {
            PendingWork? pending=null;
            ActiveWork? active=null;
            lock(_gate)
            {
                while(_pendingSessions.First is { } first)
                {
                    _pendingSessions.RemoveFirst();
                    if(!_sessions.TryGetValue(first.Value,out var session)||session.Pending is null)continue;
                    pending=session.Pending;
                    session.Pending=null;
                    session.QueueNode=null;
                    var linked=CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token,pending.RequestCancellation);
                    active=new ActiveWork(pending.Request,linked);
                    _active=active;
                    break;
                }

                if(pending is null)
                {
                    _pump=null;
                    PruneSessionsNoLock(DateTimeOffset.UtcNow);
                    return;
                }
            }

            PreviewScheduleResult result;
            try
            {
                if(active!.Cancellation.IsCancellationRequested)
                    result=PreviewScheduleResult.Cancelled(pending.Request);
                else
                    result=await _executor.ExecuteAsync(pending.Request,active.Cancellation.Token);
            }
            catch(OperationCanceledException)
            {
                result=PreviewScheduleResult.Cancelled(pending.Request);
            }
            catch(Exception e)
            {
                result=PreviewScheduleResult.Failed(pending.Request,"preview_executor_failure",SafeLogText.Sanitize(e.Message,300));
            }

            var superseded=false;
            lock(_gate)
            {
                if(ReferenceEquals(_active,active))_active=null;
                if(_sessions.TryGetValue(pending.Request.SessionId,out var session))
                {
                    session.LastSeen=DateTimeOffset.UtcNow;
                    superseded=session.LatestRevision>pending.Request.Revision;
                }
            }
            active!.Cancellation.Dispose();

            if(superseded)
            {
                Interlocked.Increment(ref _superseded);
                result=PreviewScheduleResult.Superseded(pending.Request);
            }
            pending.Completion.TrySetResult(result);
        }
    }

    private void PruneSessionsNoLock(DateTimeOffset now)
    {
        var cutoff=now-_revisionTtl;
        foreach(var key in _sessions
            .Where(row=>row.Value.Pending is null&&
                        (_active is null||!string.Equals(_active.Request.SessionId,row.Key,StringComparison.Ordinal))&&
                        row.Value.LastSeen<cutoff)
            .Select(row=>row.Key)
            .ToArray())
        {
            _sessions.Remove(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pump;
        PendingWork[] pending;
        lock(_gate)
        {
            if(_disposed)return;
            _disposed=true;
            _shutdown.Cancel();
            _active?.Cancellation.Cancel();
            pending=_sessions.Values.Where(x=>x.Pending is not null).Select(x=>x.Pending!).ToArray();
            foreach(var session in _sessions.Values){session.Pending=null;session.QueueNode=null;}
            _pendingSessions.Clear();
            pump=_pump;
        }
        foreach(var item in pending)item.Completion.TrySetResult(PreviewScheduleResult.Cancelled(item.Request));
        if(pump is not null)
        {
            try{await pump.WaitAsync(TimeSpan.FromSeconds(5));}catch{}
        }
        _shutdown.Dispose();
    }

    private void ThrowIfDisposed(){if(_disposed)throw new ObjectDisposedException(nameof(PreviewScheduler));}
    private static int ValidateMaxPending(int value)=>value is >=1 and <=16?value:throw new ArgumentOutOfRangeException(nameof(value));
    private static TimeSpan ValidateTtl(TimeSpan value)=>value>=TimeSpan.FromMinutes(1)&&value<=TimeSpan.FromHours(1)?value:throw new ArgumentOutOfRangeException(nameof(value));

    private sealed class SessionState
    {
        public long LatestRevision;
        public DateTimeOffset LastSeen=DateTimeOffset.UtcNow;
        public PendingWork? Pending;
        public LinkedListNode<string>? QueueNode;
    }

    private sealed class PendingWork
    {
        public PreviewWorkRequest Request{get;}
        public CancellationToken RequestCancellation{get;}
        public TaskCompletionSource<PreviewScheduleResult> Completion{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingWork(PreviewWorkRequest request,CancellationToken requestCancellation){Request=request;RequestCancellation=requestCancellation;}
    }

    private sealed record ActiveWork(PreviewWorkRequest Request,CancellationTokenSource Cancellation);
}
