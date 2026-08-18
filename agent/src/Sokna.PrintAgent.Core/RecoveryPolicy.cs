namespace Sokna.PrintAgent.Core;
public enum RecoveryDecision{ContinueAccept,ContinueClaimed,ReportSubmitted,RetryReport,RecoveryHold,Nothing}
public static class RecoveryPolicy
{
    public static RecoveryDecision Decide(LocalJobState state,bool workerMayHaveLaunched,bool hasSpoolerJobId)=>state switch{
        LocalJobState.Reserved=>RecoveryDecision.ContinueAccept,
        LocalJobState.Claimed=>RecoveryDecision.ContinueClaimed,
        LocalJobState.WorkerLaunching=>RecoveryDecision.RecoveryHold,
        LocalJobState.Submitted=>RecoveryDecision.ReportSubmitted,
        LocalJobState.ReportPending=>RecoveryDecision.RetryReport,
        LocalJobState.Unknown or LocalJobState.RecoveryHold=>RecoveryDecision.RetryReport,
        _=>RecoveryDecision.Nothing};
}
