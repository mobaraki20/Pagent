namespace Sokna.PrintAgent.Core;

public enum RecoveryDecision{ContinueAccept,ContinueClaimed,ReportSubmitted,RetryReport,RecoveryHold,Nothing}

public static class RecoveryPolicy
{
    // A WorkerLaunching record means execution may have escaped the service. After restart, absence of a
    // fence is not proof that the child never reached the spooler; only the live service can prove/kill
    // its child before deciding that a pre-fence failure is retryable.
    public static RecoveryDecision Decide(LocalJobState state,bool hasSubmissionFence,bool hasDurableWorkerResult)=>state switch{
        LocalJobState.Reserved=>RecoveryDecision.ContinueAccept,
        LocalJobState.Claimed=>RecoveryDecision.ContinueClaimed,
        LocalJobState.WorkerLaunching when hasDurableWorkerResult=>RecoveryDecision.Nothing,
        LocalJobState.WorkerLaunching=>RecoveryDecision.RecoveryHold,
        LocalJobState.Submitted=>RecoveryDecision.ReportSubmitted,
        LocalJobState.ReportPending=>RecoveryDecision.RetryReport,
        LocalJobState.Unknown or LocalJobState.RecoveryHold=>RecoveryDecision.RetryReport,
        _=>RecoveryDecision.Nothing};
}
