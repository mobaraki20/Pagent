namespace Sokna.PrintAgent.Core;

public enum RecoveryDecision{ContinueAccept,ContinueClaimed,ReportSubmitted,RetryReport,RecoveryHold,Nothing}

public static class RecoveryPolicy
{
    // Pure classification used by tests/documentation. The Service reads a durable Worker result first;
    // if one exists it applies that evidence directly. Without a result, the submission fence is the
    // boundary between a safe resume and an ambiguous outcome that must never auto-reprint.
    public static RecoveryDecision Decide(LocalJobState state,bool hasSubmissionFence,bool hasDurableWorkerResult)=>state switch{
        LocalJobState.Reserved=>RecoveryDecision.ContinueAccept,
        LocalJobState.Claimed=>RecoveryDecision.ContinueClaimed,
        LocalJobState.WorkerLaunching when hasDurableWorkerResult=>RecoveryDecision.Nothing,
        LocalJobState.WorkerLaunching when hasSubmissionFence=>RecoveryDecision.RecoveryHold,
        LocalJobState.WorkerLaunching=>RecoveryDecision.ContinueClaimed,
        LocalJobState.Submitted=>RecoveryDecision.ReportSubmitted,
        LocalJobState.ReportPending=>RecoveryDecision.RetryReport,
        LocalJobState.Unknown or LocalJobState.RecoveryHold=>RecoveryDecision.RetryReport,
        _=>RecoveryDecision.Nothing};
}
