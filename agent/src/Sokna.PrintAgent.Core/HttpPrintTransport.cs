using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sokna.PrintAgent.Core;

public sealed class HttpPrintTransport : IPrintTransport
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILeaseTokenProtector? _leaseProtector;

    public HttpPrintTransport(HttpClient http,string baseUrl,string token,ILeaseTokenProtector? leaseProtector=null)
    {
        _http=http;
        _baseUrl=baseUrl.TrimEnd('/');
        _http.Timeout=TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        _leaseProtector=leaseProtector ?? (OperatingSystem.IsWindows()?new DpapiLeaseTokenProtector():null);
    }

    private async Task<T> PostAsync<T>(string action,object body,CancellationToken ct)
    {
        using var response=await _http.PostAsJsonAsync($"{_baseUrl}/print-agent/v4/api.php?action={Uri.EscapeDataString(action)}",body,AgentOptions.JsonOptions(),ct);
        var text=await response.Content.ReadAsStringAsync(ct);
        var retryAfter=ReadRetryAfter(response);
        if(!response.IsSuccessStatusCode)throw ParseApiError(action,response.StatusCode,text,retryAfter);

        T result;
        try
        {
            result=JsonSerializer.Deserialize<T>(text,AgentOptions.JsonOptions())
                ?? throw new PrintProtocolException(action,"empty_success_response",$"Print API {action} پاسخ موفق خالی برگرداند.");
        }
        catch(PrintProtocolException){throw;}
        catch(JsonException e)
        {
            throw new PrintProtocolException(action,"invalid_success_json",$"Print API {action} پاسخ HTTP موفق با JSON نامعتبر/ناسازگار برگرداند.",e);
        }
        catch(NotSupportedException e)
        {
            throw new PrintProtocolException(action,"unsupported_success_json",$"Print API {action} پاسخ HTTP موفق با نوع دادهٔ پشتیبانی‌نشده برگرداند.",e);
        }

        if(result is ApiResult api && !api.Success)
        {
            throw new PrintApiException(
                response.StatusCode,
                Safe(api.Message ?? $"Print API {action} business response ناموفق بود: {api.Code ?? "unknown"}"),
                api.Code,
                api.CurrentState,
                false,
                api.RequiresHumanResolution,
                api.NextAction,
                retryAfter);
        }
        return result;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retry=response.Headers.RetryAfter;
        if(retry?.Delta is { } delta && delta>=TimeSpan.Zero)return delta;
        if(retry?.Date is { } date)
        {
            var remaining=date-DateTimeOffset.UtcNow;
            return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;
        }
        return null;
    }

    private static PrintApiException ParseApiError(string action,System.Net.HttpStatusCode status,string text,TimeSpan? retryAfter)
    {
        string? code=null;
        string? current=null;
        string? message=null;
        string? nextAction=null;
        var terminal=false;
        var human=false;
        try
        {
            using var doc=JsonDocument.Parse(text);
            var root=doc.RootElement;
            if(root.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String)code=c.GetString();
            if(root.TryGetProperty("current_state",out var s)&&s.ValueKind==JsonValueKind.String)current=s.GetString();
            if(root.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String)message=m.GetString();
            if(root.TryGetProperty("next_action",out var n)&&n.ValueKind==JsonValueKind.String)nextAction=n.GetString();
            terminal=root.TryGetProperty("terminal",out var t)&&t.ValueKind==JsonValueKind.True;
            human=root.TryGetProperty("requires_human_resolution",out var h)&&h.ValueKind==JsonValueKind.True;
        }
        catch(JsonException){}
        message=string.IsNullOrWhiteSpace(message)?$"Print API {action} HTTP {(int)status}: {Safe(text)}":message;
        return new PrintApiException(status,Safe(message!),code,current,terminal,human,nextAction,retryAfter);
    }

    private static string Safe(string text)=>SafeLogText.Sanitize(text,400);

    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)=>PostAsync<ClaimResponse>("claim",new{request_id=request.RequestId,agent_version=request.AgentVersion,protocol_version=request.ProtocolVersion,limit=request.Limit,ready_destination_keys=request.ReadyDestinationKeys},ct);

    // Compatibility wrappers preserve the pre-remediation interface for existing harnesses/adapters.
    // Production service code persists and calls the envelope overloads below.
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)
        =>AcceptAsync(item,new AcceptRequestEnvelope(requestId,AgentVersionInfo.Current,4,item.Attempt.Id,localReceiptId,item.Job.ContentSha256),ct);

    public Task<ApiResult> AcceptAsync(ClaimItem item,AcceptRequestEnvelope request,CancellationToken ct)=>PostAsync<ApiResult>("accept",new{request_id=request.RequestId,agent_version=request.AgentVersion,protocol_version=request.ProtocolVersion,attempt_id=request.AttemptId,lease_token=item.Attempt.LeaseToken,local_receipt_id=request.LocalReceiptId,content_sha256=request.ContentSha256},ct);

    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)
        =>RenewAsync(item,new RenewRequestEnvelope(requestId,AgentVersionInfo.Current,4,item.Attempt.Id),ct);

    public Task<ApiResult> RenewAsync(ClaimItem item,RenewRequestEnvelope request,CancellationToken ct)=>PostAsync<ApiResult>("renew",new{request_id=request.RequestId,agent_version=request.AgentVersion,protocol_version=request.ProtocolVersion,attempt_id=request.AttemptId,lease_token=item.Attempt.LeaseToken},ct);

    public async Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)
    {
        var result=await PostAsync<AttemptStatusResult>("attempt_status",new{agent_version=AgentVersionInfo.Current,protocol_version=4,attempt_id=job.AttemptId,lease_token=UnprotectLease(job.ProtectedLeaseToken),local_receipt_id=job.LocalReceiptId},ct);
        AttemptStatusContract.ValidateForJob(result,job);
        return result;
    }

    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)
        =>StartAsync(job,new StartRequestEnvelope(requestId,AgentVersionInfo.Current,4,job.AttemptId),ct);

    public Task<ApiResult> StartAsync(LocalJob job,StartRequestEnvelope request,CancellationToken ct)=>PostAsync<ApiResult>("start",new{request_id=request.RequestId,agent_version=request.AgentVersion,protocol_version=request.ProtocolVersion,attempt_id=request.AttemptId,lease_token=UnprotectLease(job.ProtectedLeaseToken)},ct);

    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)=>PostAsync<ApiResult>("report",new{request_id=request.RequestId,agent_version=request.AgentVersion,protocol_version=request.ProtocolVersion,attempt_id=request.AttemptId,lease_token=UnprotectLease(job.ProtectedLeaseToken),local_receipt_id=request.LocalReceiptId,status=request.Status,spooler_job_id=request.SpoolerJobId,retryable=request.Retryable,error_code=request.ErrorCode,error_message=request.ErrorMessage},ct);

    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload p,CancellationToken ct)=>PostAsync<ApiResult>("heartbeat",new{request_id=p.RequestId,agent_version=AgentVersionInfo.Current,protocol_version=4,hostname=p.Hostname,os_version=p.OsVersion,uptime_seconds=p.UptimeSeconds,last_poll_success_at=p.LastPollSuccessAt,local_backlog_count=p.LocalBacklogCount,local_unknown_count=p.LocalUnknownCount,last_submission_at=p.LastSubmissionAt,sqlite_health=p.SqliteHealth,disk_free_mb=p.DiskFreeMb,worker_ok=p.WorkerOk,config_ok=p.ConfigOk,instance_lock_ok=p.InstanceLockOk,printers=p.Printers,last_successful_action=p.LastSuccessfulAction,last_api_success_at=p.LastApiSuccessAt,last_api_error_code=p.LastApiErrorCode,consecutive_api_failures=p.ConsecutiveApiFailures,last_api_latency_ms=p.LastApiLatencyMs,printer_discovery_at=p.PrinterDiscoveryAt,bridge_protocol_version=p.BridgeProtocolVersion,bridge_port=p.BridgePort,bridge_pairing_id=p.BridgePairingId,bridge_origin=p.BridgeOrigin,pending_report_count=p.PendingReportCount,auth_blocked_report_count=p.AuthBlockedReportCount,reconciliation_report_count=p.ReconciliationReportCount},ct);

    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>PostAsync<ProbeResponse>("probe",new{agent_version=AgentVersionInfo.Current,protocol_version=4},ct);

    private string UnprotectLease(string value)=>_leaseProtector?.Unprotect(value)??throw new PlatformNotSupportedException("Lease token unprotect requires Windows DPAPI or an injected ILeaseTokenProtector.");
}
