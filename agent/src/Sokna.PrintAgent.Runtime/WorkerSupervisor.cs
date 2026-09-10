namespace Sokna.PrintAgent.Service;

public sealed record WorkerLaunchSpec(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    TimeSpan ExecutionTimeout,
    TimeSpan ExitProofTimeout,
    TimeSpan ShutdownExitProofTimeout,
    int StandardErrorLimit=4096);

public enum WorkerStopKind
{
    Exited,
    LaunchFailed,
    GuardFailed,
    StartSignalFailed,
    ExecutionTimeout,
    ServiceShutdown,
    ExitUnproven
}

public sealed record WorkerSupervisionResult(
    WorkerStopKind StopKind,
    bool ProcessStarted,
    bool ExitProven,
    int? ExitCode,
    string StandardError,
    string? Error)
{
    public bool SafeNoChildCreated => StopKind==WorkerStopKind.LaunchFailed&&!ProcessStarted;
}

public interface IWorkerProcess : IAsyncDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    void AttachGuard();
    void KillTree();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    string GetBoundedStandardError();
}

public interface IWorkerProcessFactory
{
    IWorkerProcess Start(WorkerLaunchSpec spec);
}

/// <summary>
/// Owns the bounded lifecycle of one isolated print worker. It never decides print outcome;
/// callers combine exit proof with the durable worker result and submission fence.
/// </summary>
public sealed class WorkerSupervisor
{
    private readonly IWorkerProcessFactory _factory;

    public WorkerSupervisor(IWorkerProcessFactory factory)=>_factory=factory;

    public async Task<WorkerSupervisionResult> RunAsync(
        WorkerLaunchSpec spec,
        Func<CancellationToken,Task> afterGuardReady,
        CancellationToken serviceCancellation)
    {
        ArgumentNullException.ThrowIfNull(afterGuardReady);
        IWorkerProcess? process=null;
        try
        {
            try
            {
                process=_factory.Start(spec);
            }
            catch(Exception e)
            {
                return new(WorkerStopKind.LaunchFailed,false,true,null,string.Empty,Safe(e.Message));
            }

            try
            {
                process.AttachGuard();
            }
            catch(Exception e)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.GuardFailed,e,serviceCancellation.IsCancellationRequested);
            }

            try
            {
                await afterGuardReady(serviceCancellation);
            }
            catch(OperationCanceledException) when(serviceCancellation.IsCancellationRequested)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.ServiceShutdown,null,true);
            }
            catch(Exception e)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.StartSignalFailed,e,serviceCancellation.IsCancellationRequested);
            }

            using var executionTimeout=new CancellationTokenSource(spec.ExecutionTimeout);
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(serviceCancellation,executionTimeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                return new(WorkerStopKind.Exited,true,true,process.ExitCode,Bound(process.GetBoundedStandardError(),spec.StandardErrorLimit),null);
            }
            catch(OperationCanceledException) when(serviceCancellation.IsCancellationRequested)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.ServiceShutdown,null,true);
            }
            catch(OperationCanceledException)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.ExecutionTimeout,null,false);
            }
            catch(Exception e)
            {
                return await StopAndProveAsync(process,spec,WorkerStopKind.ExitUnproven,e,serviceCancellation.IsCancellationRequested);
            }
        }
        finally
        {
            if(process is not null)await process.DisposeAsync();
        }
    }

    private static async Task<WorkerSupervisionResult> StopAndProveAsync(
        IWorkerProcess process,
        WorkerLaunchSpec spec,
        WorkerStopKind requestedKind,
        Exception? originalFailure,
        bool shuttingDown)
    {
        Exception? killFailure=null;
        try
        {
            if(!process.HasExited)process.KillTree();
        }
        catch(Exception e)
        {
            killFailure=e;
        }

        var proofTimeout=shuttingDown?spec.ShutdownExitProofTimeout:spec.ExitProofTimeout;
        var exitProven=process.HasExited;
        if(!exitProven)
        {
            using var proof=new CancellationTokenSource(proofTimeout);
            try
            {
                await process.WaitForExitAsync(proof.Token);
                exitProven=process.HasExited;
            }
            catch(OperationCanceledException){}
            catch(Exception e){killFailure??=e;}
        }

        var error=JoinErrors(originalFailure,killFailure);
        var actualKind=exitProven?requestedKind:WorkerStopKind.ExitUnproven;
        return new(
            actualKind,
            true,
            exitProven,
            exitProven?process.ExitCode:null,
            Bound(process.GetBoundedStandardError(),spec.StandardErrorLimit),
            error);
    }

    private static string? JoinErrors(Exception? first,Exception? second)
    {
        if(first is null&&second is null)return null;
        if(first is null)return Safe(second!.Message);
        if(second is null)return Safe(first.Message);
        return Safe(first.Message+" | "+second.Message);
    }

    private static string Safe(string value)=>Core.SafeLogText.Sanitize(value,400);
    private static string Bound(string value,int limit)
    {
        if(string.IsNullOrEmpty(value))return string.Empty;
        var safe=Core.SafeLogText.Sanitize(value,Math.Max(64,limit));
        return safe.Length<=limit?safe:safe[..limit];
    }
}
