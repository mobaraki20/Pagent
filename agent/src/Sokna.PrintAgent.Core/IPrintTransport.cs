namespace Sokna.PrintAgent.Core;
public interface IPrintTransport
{
    Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct);
    Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct);
    Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct);
    Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct);
    Task<ApiResult> ReportAsync(LocalJob job,string requestId,string status,string? spoolerJobId,bool retryable,string? errorCode,string? errorMessage,CancellationToken ct);
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
    long? LastApiLatencyMs=null);
public sealed record ProbeResponse(bool Success,int ProtocolVersion,string MinimumAgentVersion,string RecommendedAgentVersion,List<DestinationConfig> Destinations);
