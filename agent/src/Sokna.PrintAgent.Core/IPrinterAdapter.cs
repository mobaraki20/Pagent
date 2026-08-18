namespace Sokna.PrintAgent.Core;
public interface IPrinterAdapter
{
    Task<WorkerResult> SubmitAsync(WorkerInput input,CancellationToken ct);
}
public interface IPrinterHealthProvider
{
    IReadOnlyList<PrinterQueueHealth> GetQueues();
}
