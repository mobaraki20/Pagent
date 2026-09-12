namespace Sokna.PrintAgent.Core;

public interface IPrintTransport
{
    Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct);
    Task<ClaimConflictResolutionResult> ResolveClaimConflictAsync(ClaimConflictResolutionRequest request,CancellationToken ct)
        =>Task.FromException<ClaimConflictResolutionResult>(new NotSupportedException("Server/transport فاقد claim_conflict_rekey_v1 است."));

    // Transitional source-compatibility overload for existing test/adaptor implementations.
    // Production coordinator code must use the durable request-envelope overload below.
    Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct);
    Task<ApiResult> AcceptAsync(ClaimItem item,AcceptRequestEnvelope request,CancellationToken ct)
        => AcceptAsync(item,request.LocalReceiptId,request.RequestId,ct);

    // Transitional source-compatibility overload; see AcceptAsync above.
    Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct);
    Task<ApiResult> RenewAsync(ClaimItem item,RenewRequestEnvelope request,CancellationToken ct)
        => RenewAsync(item,request.RequestId,ct);

    Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct);

    // Transitional source-compatibility overload; see AcceptAsync above.
    Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct);
    Task<ApiResult> StartAsync(LocalJob job,StartRequestEnvelope request,CancellationToken ct)
        => StartAsync(job,request.RequestId,ct);

    Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct);
    Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct);
    Task<ProbeResponse> ProbeAsync(CancellationToken ct);
}

public sealed record ClaimRequestEnvelope(
    string RequestId,
    string AgentVersion,
    int ProtocolVersion,
    string[] ReadyDestinationKeys,
    int Limit,
    string CreatedAt);

public sealed record AcceptRequestEnvelope(
    string RequestId,
    string AgentVersion,
    int ProtocolVersion,
    long AttemptId,
    string LocalReceiptId,
    string ContentSha256);

public sealed record RenewRequestEnvelope(
    string RequestId,
    string AgentVersion,
    int ProtocolVersion,
    long AttemptId);

public sealed record StartRequestEnvelope(
    string RequestId,
    string AgentVersion,
    int ProtocolVersion,
    long AttemptId);

public sealed record HeartbeatPayload(
    string RequestId,
    string Hostname,
    string AgentVersion,
    string OsVersion,
    long UptimeSeconds,
    string? LastPollSuccessAt,
    int LocalBacklogCount,
    int LocalUnknownCount,
    string? LastSubmissionAt,
    string SqliteHealth,
    long DiskFreeMb,
    bool WorkerOk,
    bool ConfigOk,
    bool InstanceLockOk,
    List<PrinterQueueHealth> Printers,
    string? LastSuccessfulAction=null,
    string? LastApiSuccessAt=null,
    string? LastApiErrorCode=null,
    int ConsecutiveApiFailures=0,
    long? LastApiLatencyMs=null,
    string? PrinterDiscoveryAt=null,
    int BridgeProtocolVersion=0,
    int BridgePort=0,
    string? BridgePairingId=null,
    string? BridgeOrigin=null,
    int PendingReportCount=0,
    int AuthBlockedReportCount=0,
    int ReconciliationReportCount=0,
    string? PrinterDiscoveryLastFailureAt=null,
    string? PrinterDiscoveryError=null,
    long? PrinterDiscoveryAgeMilliseconds=null,
    bool PrinterDiscoveryFresh=false,
    long PrinterDiscoveryGeneration=0);

public sealed record ProbeResponse(
    bool Success,
    int ProtocolVersion,
    string MinimumAgentVersion,
    string RecommendedAgentVersion,
    List<DestinationConfig> Destinations,
    string[]? Capabilities=null,
    string? ServerInstanceId=null,
    string? ServerTime=null);

public sealed record AttemptStatusResult(bool Success,long AttemptId,long JobId,string AttemptState,string JobState,bool ReceiptMatches,string NextAction,bool Terminal,bool RequiresHumanResolution,string? LeaseExpiresAt,string ServerTime);
