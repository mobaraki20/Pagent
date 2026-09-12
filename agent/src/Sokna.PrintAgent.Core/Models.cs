using System.Text.Json.Serialization;

namespace Sokna.PrintAgent.Core;

public sealed record DestinationConfig(
    [property:JsonPropertyName("destination_key")] string DestinationKey,
    [property:JsonPropertyName("label")] string Label,
    [property:JsonPropertyName("windows_queue_name")] string WindowsQueueName,
    [property:JsonPropertyName("paper_width_mm")] double PaperWidthMm,
    [property:JsonPropertyName("printable_width_mm")] double PrintableWidthMm,
    [property:JsonPropertyName("copies")] int Copies,
    [property:JsonPropertyName("layout_mode")] string LayoutMode);

public sealed record ClaimedJob(int Id,string PublicToken,string JobType,bool Required,string? EntityType,string? EntityId,string CreatedAt,int ContractVersion,string ContentSha256,string PayloadJson);
public sealed record ClaimAttempt(long Id,int AttemptNo,string LeaseToken,string LeaseExpiresAt);
public sealed record ClaimItem(ClaimedJob Job,ClaimAttempt Attempt,DestinationConfig Destination);
public sealed record ClaimResponse(bool Success,string RequestId,List<ClaimItem> Jobs,string ServerTime,bool Idempotent);

public sealed record ApiResult(
    bool Success,
    string? Status=null,
    bool Idempotent=false,
    string? Code=null,
    string? Message=null,
    bool RequiresHumanResolution=false,
    long? AttemptId=null,
    long? JobId=null,
    string? LocalReceiptId=null,
    string? NextAction=null,
    string? CurrentState=null,
    string? ServerTime=null);

public enum LocalJobState
{
    Reserved,
    Claimed,
    WorkerLaunching,
    Submitted,
    ReportPending,
    SafeFailed,
    Unknown,
    RecoveryHold,
    Resolved
}


public enum ClaimPersistenceDisposition
{
    Created,
    ExactReplay,
    ReconciliationRequired
}

public sealed record ClaimPersistenceResult(
    ClaimPersistenceDisposition Disposition,
    LocalJob ExistingOrCreated,
    IReadOnlyList<string> MismatchedFields);

public sealed class ClaimReconciliationRequiredException : Exception
{
    public IReadOnlyList<string> MismatchedFields { get; }
    public ClaimReconciliationRequiredException(IReadOnlyList<string> mismatchedFields)
        : base("Claim تکراری نیازمند reconciliation است؛ identity/payload/destination/server برای همان attempt_id یکسان نیست.")
        => MismatchedFields=mismatchedFields;
}

public enum PrintOutcomeStatus
{
    Submitted,
    Failed,
    Unknown,
    RecoveryHold
}

public enum ReportDeliveryState
{
    Pending,
    Backoff,
    AuthBlocked,
    ReconciliationRequired,
    Delivered
}

public sealed record LocalJob(
    long ServerJobId,
    long AttemptId,
    int AttemptNo,
    string DestinationKey,
    string QueueName,
    double PaperWidthMm,
    double PrintableWidthMm,
    int Copies,
    string LayoutMode,
    string PayloadJson,
    string ContentSha256,
    string LocalReceiptId,
    string ProtectedLeaseToken,
    DateTimeOffset LeaseExpiresAt,
    LocalJobState State,
    string? SpoolerJobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? WorkerLaunchingAt,
    string? LastError,
    string ServerScope="legacy-unbound");

public sealed record AttemptOutcomeDraft(
    PrintOutcomeStatus Status,
    string? SpoolerJobId,
    bool Retryable,
    string? ErrorCode,
    string? ErrorMessage,
    string EvidenceProvenance);

public sealed record AttemptOutcome(
    long AttemptId,
    long ServerJobId,
    PrintOutcomeStatus Status,
    string? SpoolerJobId,
    bool Retryable,
    string? ErrorCode,
    string? ErrorMessage,
    string EvidenceProvenance,
    DateTimeOffset CommittedAt);

public sealed record ReportRequestEnvelope(
    string RequestId,
    string AgentVersion,
    int ProtocolVersion,
    long AttemptId,
    string LocalReceiptId,
    string Status,
    string? SpoolerJobId,
    bool Retryable,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ReportOutboxRow(
    long Id,
    long JobId,
    long AttemptId,
    string RequestId,
    string BodyJson,
    string AgentVersion,
    string ServerScope,
    ReportDeliveryState DeliveryState,
    int ErrorCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt,
    int? LastHttpStatus,
    string? LastErrorCode,
    string? LastError);

public sealed record ReportStateCounts(int Pending,int Backoff,int AuthBlocked,int ReconciliationRequired);

public sealed record WorkerInput(long ServerJobId,long AttemptId,string LocalReceiptId,string QueueName,string PayloadJson,string ContentSha256,double PaperWidthMm,double PrintableWidthMm,int Copies,string ResultPath,string FencePath,string StartSignalPath);
public sealed record WorkerResult(long ServerJobId,long AttemptId,string LocalReceiptId,string ContentSha256,string Status,string? SpoolerJobId=null,bool Retryable=false,string? ErrorCode=null,string? ErrorMessage=null);
public sealed record PrinterQueueHealth(string Name,bool Offline,bool Paused,bool PaperOut,bool Error,int Jobs,string Driver,string Port);

public sealed record LocalHealthSnapshot(
    string AgentVersion,
    string Hostname,
    string State,
    bool ConfigOk,
    bool SecretOk,
    bool ServiceAccountContext,
    string? LastError,
    string UpdatedAt,
    int LocalBacklogCount,
    int LocalUnknownCount,
    List<PrinterQueueHealth> Printers,
    string? LastSuccessfulAction=null,
    string? LastApiSuccessAt=null,
    string? LastApiErrorCode=null,
    int ConsecutiveApiFailures=0,
    long? LastApiLatencyMs=null,
    int PendingReportCount=0,
    int BackoffReportCount=0,
    int AuthBlockedReportCount=0,
    int ReconciliationReportCount=0,
    long? OldestUndeliveredReportAgeSeconds=null,
    string? PrinterDiscoveryLastSuccessAt=null,
    string? PrinterDiscoveryLastFailureAt=null,
    string? PrinterDiscoveryError=null,
    long? PrinterDiscoveryAgeMilliseconds=null,
    bool PrinterDiscoveryFresh=false,
    long PrinterDiscoveryGeneration=0,
    string TransportState="unknown",
    string? LastTransportSuccessAt=null,
    string? LastTransportErrorCode=null,
    int ConsecutiveTransportFailures=0,
    string CoordinatorState="unknown",
    string? LastCoordinatorSuccessAt=null,
    string? LastCoordinatorErrorCode=null,
    bool ClaimReconciliationRequired=false,
    int ClaimConflictCount=0,
    long? OldestClaimConflictAgeSeconds=null);
