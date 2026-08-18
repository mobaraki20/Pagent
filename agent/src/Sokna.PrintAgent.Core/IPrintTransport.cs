namespace Sokna.PrintAgent.Core;
public interface IPrintTransport
{
    Task<ClaimResponse> ClaimAsync(string requestId,IReadOnlyCollection<string> readyDestinations,int limit,CancellationToken ct);
    Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct);
    Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct);
    Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct);
    Task<ApiResult> ReportAsync(LocalJob job,string requestId,string status,string? spoolerJobId,bool retryable,string? errorCode,string? errorMessage,CancellationToken ct);
    Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct);
    Task<ProbeResponse> ProbeAsync(CancellationToken ct);
}
public sealed record HeartbeatPayload(string RequestId,string Hostname,string AgentVersion,string OsVersion,long UptimeSeconds,string? LastPollSuccessAt,int LocalBacklogCount,int LocalUnknownCount,string? LastSubmissionAt,string SqliteHealth,long DiskFreeMb,bool WorkerOk,bool ConfigOk,bool InstanceLockOk,List<PrinterQueueHealth> Printers);
public sealed record ProbeResponse(bool Success,int ProtocolVersion,string MinimumAgentVersion,string RecommendedAgentVersion,List<DestinationConfig> Destinations);
