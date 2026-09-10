using System.Diagnostics;

namespace Sokna.PrintAgent.Service;

/// <summary>
/// Runs at most one instance of a side-I/O operation. Completion is observed by the coordinator,
/// so background work never mutates coordinator-owned state directly. The completion pulse only
/// wakes the owner; it does not grant permission to print or create a job.
/// </summary>
internal sealed class SingleFlightWork<T>
{
    private Task<SingleFlightResult<T>>? _task;

    public bool IsRunning=>_task is {IsCompleted:false};
    public bool HasCompleted=>_task is {IsCompleted:true};

    public bool TryStart(Func<CancellationToken,Task<T>> work,CancellationToken ct,Action completionPulse)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(completionPulse);
        if(_task is not null)return false;
        _task=Task.Run(async()=>
        {
            var sw=Stopwatch.StartNew();
            try
            {
                var value=await work(ct);
                sw.Stop();
                return new SingleFlightResult<T>(value,null,sw.ElapsedMilliseconds);
            }
            catch(Exception e)
            {
                sw.Stop();
                return new SingleFlightResult<T>(default,e,sw.ElapsedMilliseconds);
            }
            finally
            {
                try{completionPulse();}catch{}
            }
        },CancellationToken.None);
        return true;
    }

    public bool TryTakeCompleted(out SingleFlightResult<T>? result)
    {
        result=null;
        if(_task is not {IsCompleted:true} completed)return false;
        _task=null;
        result=completed.GetAwaiter().GetResult();
        return true;
    }
}

internal sealed record SingleFlightResult<T>(T? Value,Exception? Error,long ElapsedMilliseconds)
{
    public bool Success=>Error is null;
}
