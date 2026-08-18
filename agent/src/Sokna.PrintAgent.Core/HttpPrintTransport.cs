using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace Sokna.PrintAgent.Core;
public sealed class HttpPrintTransport: IPrintTransport
{
    private readonly HttpClient _http;private readonly string _baseUrl;private readonly ILeaseTokenProtector? _leaseProtector;private const string AgentVersion="6.0.0";
    public HttpPrintTransport(HttpClient http,string baseUrl,string token,ILeaseTokenProtector? leaseProtector=null){_http=http;_baseUrl=baseUrl.TrimEnd('/');_http.Timeout=TimeSpan.FromSeconds(10);_http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);_leaseProtector=leaseProtector ?? (OperatingSystem.IsWindows()?new DpapiLeaseTokenProtector():null);}
    private async Task<T> PostAsync<T>(string action,object body,CancellationToken ct)
    {
        using var r=await _http.PostAsJsonAsync($"{_baseUrl}/print-agent/v4/api.php?action={Uri.EscapeDataString(action)}",body,AgentOptions.JsonOptions(),ct);
        var text=await r.Content.ReadAsStringAsync(ct);
        if(!r.IsSuccessStatusCode)throw ParseApiError(action,r.StatusCode,text);
        return JsonSerializer.Deserialize<T>(text,AgentOptions.JsonOptions())??throw new InvalidDataException($"Print API {action} JSON نامعتبر است.");
    }
    private static PrintApiException ParseApiError(string action,System.Net.HttpStatusCode status,string text)
    {
        string? code=null,current=null,message=null;var terminal=false;var human=false;
        try
        {
            using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
            if(root.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String)code=c.GetString();
            if(root.TryGetProperty("current_state",out var s)&&s.ValueKind==JsonValueKind.String)current=s.GetString();
            if(root.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String)message=m.GetString();
            terminal=root.TryGetProperty("terminal",out var t)&&t.ValueKind==JsonValueKind.True;
            human=root.TryGetProperty("requires_human_resolution",out var h)&&h.ValueKind==JsonValueKind.True;
        }
        catch(JsonException){}
        message=string.IsNullOrWhiteSpace(message)?$"Print API {action} HTTP {(int)status}: {Safe(text)}":message;
        return new PrintApiException(status,Safe(message!),code,current,terminal,human);
    }
    private static string Safe(string text)=>text.Length>400?text[..400]:text;
    public Task<ClaimResponse> ClaimAsync(string requestId,IReadOnlyCollection<string> readyDestinations,int limit,CancellationToken ct)=>PostAsync<ClaimResponse>("claim",new{request_id=requestId,agent_version=AgentVersion,protocol_version=4,limit,ready_destination_keys=readyDestinations},ct);
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>PostAsync<ApiResult>("accept",new{request_id=requestId,agent_version=AgentVersion,protocol_version=4,attempt_id=item.Attempt.Id,lease_token=item.Attempt.LeaseToken,local_receipt_id=localReceiptId,content_sha256=item.Job.ContentSha256},ct);
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>PostAsync<ApiResult>("renew",new{request_id=requestId,agent_version=AgentVersion,protocol_version=4,attempt_id=item.Attempt.Id,lease_token=item.Attempt.LeaseToken},ct);
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)=>PostAsync<ApiResult>("start",new{request_id=requestId,agent_version=AgentVersion,protocol_version=4,attempt_id=job.AttemptId,lease_token=UnprotectLease(job.ProtectedLeaseToken)},ct);
    public Task<ApiResult> ReportAsync(LocalJob job,string requestId,string status,string? spoolerJobId,bool retryable,string? errorCode,string? errorMessage,CancellationToken ct)=>PostAsync<ApiResult>("report",new{request_id=requestId,agent_version=AgentVersion,protocol_version=4,attempt_id=job.AttemptId,lease_token=UnprotectLease(job.ProtectedLeaseToken),local_receipt_id=job.LocalReceiptId,status,spooler_job_id=spoolerJobId,retryable,error_code=errorCode,error_message=errorMessage},ct);
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload p,CancellationToken ct)=>PostAsync<ApiResult>("heartbeat",new{request_id=p.RequestId,agent_version=AgentVersion,protocol_version=4,hostname=p.Hostname,os_version=p.OsVersion,uptime_seconds=p.UptimeSeconds,last_poll_success_at=p.LastPollSuccessAt,local_backlog_count=p.LocalBacklogCount,local_unknown_count=p.LocalUnknownCount,last_submission_at=p.LastSubmissionAt,sqlite_health=p.SqliteHealth,disk_free_mb=p.DiskFreeMb,worker_ok=p.WorkerOk,config_ok=p.ConfigOk,instance_lock_ok=p.InstanceLockOk,printers=p.Printers},ct);
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>PostAsync<ProbeResponse>("probe",new{agent_version=AgentVersion,protocol_version=4},ct);

    private string UnprotectLease(string value)=>_leaseProtector?.Unprotect(value)??throw new PlatformNotSupportedException("Lease token unprotect requires Windows DPAPI or an injected ILeaseTokenProtector.");
}
