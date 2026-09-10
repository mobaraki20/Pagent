namespace Sokna.PrintAgent.Core;

public interface IPrinterAdapter
{
    Task<WorkerResult> SubmitAsync(WorkerInput input,CancellationToken ct);
}

/// <summary>Raw operating-system printer discovery. It may block or fail and is never called by the print coordinator directly.</summary>
public interface IPrinterHealthProvider
{
    IReadOnlyList<PrinterQueueHealth> GetQueues();
}

/// <summary>Coordinator-facing, cached and age-aware printer health.</summary>
public interface IPrinterHealthReader
{
    PrinterHealthSnapshot Read(TimeSpan freshnessWindow);
}

public sealed record PrinterHealthSnapshot(
    IReadOnlyList<PrinterQueueHealth> Queues,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    long? AgeMilliseconds,
    bool IsFresh,
    long Generation)
{
    public static PrinterHealthSnapshot Unavailable(string? error=null)
        =>new([],null,null,error,null,false,0);
}
