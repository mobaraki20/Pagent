using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||string.IsNullOrWhiteSpace(caseId))
{
    Console.Error.WriteLine("Usage: --case A01 --results <directory>");
    return 64;
}
if(!parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("--results is required.");
    return 64;
}
Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,$"{caseId}.log");
var resultPath=Path.Combine(resultsDirectory,$"{caseId}.result.json");
var assertions=new List<string>();
var failures=new List<string>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    switch(caseId.ToUpperInvariant())
    {
        case "A01": await RunA01(); break;
        case "A04": await RunA04(); break;
        case "A06": await RunA06(); break;
        case "A07": RunA07(); break;
        case "A08": RunA08(); break;
        case "A09": RunA09(); break;
        case "A10": await RunA10(); break;
        case "A44": await RunA44(); break;
        case "A46": await RunA46(); break;
        default:
            await WriteResult("NOT_RUN",3,$"Case {caseId} is not implemented in the harness yet.");
            return 3;
    }

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }

    Log($"PASS {caseId}; assertions={assertions.Count}");
    await WriteResult("PASS",0,null);
    return 0;
}
catch(Exception e)
{
    Log($"FAIL {e.GetType().Name}: {SafeLogText.Sanitize(e.Message,400)}");
    await WriteResult("FAIL",1,$"{e.GetType().Name}: {SafeLogText.Sanitize(e.Message,400)}");
    return 1;
}

async Task RunA01()
{
    using var env=await TestEnvironment.CreateAsync("a01");
    var job=await env.CreateJobAsync(2001,"server-a","receipt-a01");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_open_failed","safe pre-fence failure","worker-result-pre-fence");
    var request=env.Report(job,"a01-report","failed",null,true,"printer_open_failed","safe pre-fence failure");
    var outbox=await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    await env.Store.MarkReportDeliveryAsync(outbox.Id,ReportDeliveryState.ReconciliationRequired,"quarantined",409,"report_conflict",null);

    var restarted=new LocalQueueStore(env.DatabasePath,env.Protector);
    await restarted.InitializeAsync();
    var outcome=await restarted.GetOutcomeAsync(job.AttemptId);
    var report=await restarted.GetOutboxForAttemptAsync(job.AttemptId);
    var wire=JsonSerializer.Deserialize<ReportRequestEnvelope>(report!.BodyJson,AgentOptions.JsonOptions());
    Check(outcome?.Status==PrintOutcomeStatus.Failed,"failed outcome survives restart");
    Check(report.DeliveryState==ReportDeliveryState.ReconciliationRequired,"quarantine survives restart");
    Check(wire?.Status=="failed","wire status remains failed");
    Check(wire?.SpoolerJobId is null,"failed report does not invent spooler id");
    Check(await restarted.HasPendingReportAsync(job.AttemptId),"quarantined report remains durable evidence");
    Check((await restarted.GetOutcomeAsync(job.AttemptId))?.Status!=PrintOutcomeStatus.Submitted,"restart never synthesizes submitted");
}

async Task RunA04()
{
    using var env=await TestEnvironment.CreateAsync("a04");
    var job=await env.CreateJobAsync(2004,"server-a","receipt-a04");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_offline","offline","worker-result-pre-fence");
    var request=env.Report(job,"a04-report","failed",null,true,"printer_offline","offline");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var transport=new ScriptedTransport([FakeReportMode.CommitThenDisconnect,FakeReportMode.Success]);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);
    var first=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    Check(first.Backoff==1,"lost ACK creates backoff");
    var row=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(row?.DeliveryState==ReportDeliveryState.Backoff,"outbox remains undelivered after disconnect");
    await env.Store.MarkReportDeliveryAsync(row!.Id,ReportDeliveryState.Pending,"manual test clock advance",null,"test_retry",DateTimeOffset.UtcNow.AddSeconds(-1));
    var second=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    Check(second.Delivered==1,"same report is ACKed on replay");
    Check(transport.ReportRequests.Count==2,"report transport invoked twice");
    Check(transport.ReportRequests[0]==transport.ReportRequests[1],"same request id and body replayed");
    Check(transport.PrintInvocationCount==0,"report retry never invokes printing");
}

async Task RunA06()
{
    using var env=await TestEnvironment.CreateAsync("a06");
    var job=await env.CreateJobAsync(2006,"server-a","receipt-a06");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_offline","offline","worker-result-pre-fence");
    var request=env.Report(job,"a06-report","failed",null,true,"printer_offline","offline");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var transport=new ScriptedTransport([FakeReportMode.Unauthorized,FakeReportMode.Success]);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);
    var first=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var blocked=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(first.AuthBlocked==1,"401 sets auth blocked");
    Check(blocked?.DeliveryState==ReportDeliveryState.AuthBlocked,"durable auth blocked state");
    var resumed=await dispatcher.ResumeAfterCredentialProbeAsync("server-a",CancellationToken.None);
    Check(resumed==1,"valid credential probe resumes exactly one report");
    var second=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var delivered=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(second.Delivered==1,"repaired credential delivers report");
    Check(delivered?.DeliveryState==ReportDeliveryState.Delivered,"report marked delivered");
    Check(transport.ReportRequests.Count==2&&transport.ReportRequests[0]==transport.ReportRequests[1],"credential repair preserves report identity/body");
    Check(transport.PrintInvocationCount==0,"credential recovery never reprints");
}

void RunA07()
{
    var policy=new ReportDeliveryPolicy(jitter:()=>0.5);
    var auth=policy.ForApiException(new PrintApiException(HttpStatusCode.Forbidden,"revoked","token_revoked"),0,"failed");
    var policyDenied=policy.ForApiException(new PrintApiException(HttpStatusCode.Forbidden,"policy","destination_forbidden"),0,"failed");
    var genericAuth=policy.ForApiException(new PrintApiException(HttpStatusCode.Forbidden,"auth","invalid_token"),0,"failed");
    Check(auth.State==ReportDeliveryState.AuthBlocked&&auth.StopCurrentServerScope,"revocation blocks auth scope");
    Check(genericAuth.State==ReportDeliveryState.AuthBlocked,"auth 403 classified separately");
    Check(policyDenied.State==ReportDeliveryState.ReconciliationRequired&&!policyDenied.StopCurrentServerScope,"policy 403 requires reconciliation without auth loop");
}

void RunA08()
{
    var policy=new ReportDeliveryPolicy(jitter:()=>0.5);
    foreach(var status in new[]{HttpStatusCode.RequestTimeout,(HttpStatusCode)429,HttpStatusCode.ServiceUnavailable})
    {
        var decision=policy.ForApiException(new PrintApiException(status,"transient","retry_later",retryAfter:TimeSpan.FromSeconds(30)),2,"failed");
        Check(decision.State==ReportDeliveryState.Backoff,$"{(int)status} uses backoff");
        Check(decision.NextAttemptAt>DateTimeOffset.UtcNow.AddSeconds(20),$"{(int)status} honors Retry-After");
        Check(!decision.StopCurrentServerScope,$"{(int)status} does not auth-block server scope");
    }
}

void RunA09()
{
    var policy=new ReportDeliveryPolicy(jitter:()=>0.5);
    var duplicate=policy.ForApiException(new PrintApiException(HttpStatusCode.Conflict,"already","already_reported","failed"),0,"failed");
    var mismatch=policy.ForApiException(new PrintApiException(HttpStatusCode.Conflict,"already","already_reported","submitted"),0,"failed");
    var human=policy.ForApiException(new PrintApiException(HttpStatusCode.Conflict,"human","already_reported","failed",requiresHumanResolution:true),0,"failed");
    Check(duplicate.TreatAsDelivered&&duplicate.State==ReportDeliveryState.Delivered,"matching idempotent conflict ACKs");
    Check(!mismatch.TreatAsDelivered&&mismatch.State==ReportDeliveryState.ReconciliationRequired,"state mismatch is not ACK");
    Check(!human.TreatAsDelivered&&human.State==ReportDeliveryState.ReconciliationRequired,"human-resolution conflict is not ACK");
}

async Task RunA10()
{
    using var env=await TestEnvironment.CreateAsync("a10");
    var job=await env.CreateJobAsync(2010,"server-a","receipt-a10");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"schema","schema","worker-result-pre-fence");
    var request=env.Report(job,"a10-report","failed",null,true,"schema","schema");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var transport=new ScriptedTransport([FakeReportMode.Unprocessable]);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);
    var summary=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var row=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    Check(summary.ReconciliationRequired==1,"422 requires reconciliation");
    Check(row?.DeliveryState==ReportDeliveryState.ReconciliationRequired,"422 evidence remains durable");
    Check(outcome?.Status==PrintOutcomeStatus.Failed,"422 does not mutate print outcome");
}

async Task RunA44()
{
    using var env=await TestEnvironment.CreateAsync("a44");
    var job=await env.CreateJobAsync(2044,"server-a","receipt-a44");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"offline","offline","worker-result-pre-fence");
    var request=env.Report(job,"stable-a44",AgentVersionInfo.Current,"failed",null,true,"offline","offline");
    var first=await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var changedVersion=request with{AgentVersion="99.0.0",RequestId="different-request-id"};
    var replay=await env.Store.CommitOutcomeAndReportAsync(job,draft,changedVersion);
    Check(replay.RequestId==first.RequestId,"upgrade replay keeps first request id");
    Check(replay.BodyJson==first.BodyJson,"upgrade replay keeps first wire body");

    var conflictingClaim=env.MakeClaim(job.AttemptId,job.AttemptNo,"different-destination");
    await ExpectThrowsAsync(()=>env.Store.PersistReservedAsync(conflictingClaim,"receipt-conflict","server-a"),"attempt collision with different destination rejected");
}

async Task RunA46()
{
    using var env=await TestEnvironment.CreateAsync("a46");
    var job=await env.CreateJobAsync(2046,"server-old","receipt-a46");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"offline","offline","worker-result-pre-fence");
    var request=env.Report(job,"a46-report","failed",null,true,"offline","offline");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var transport=new ScriptedTransport([FakeReportMode.Success]);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);
    var summary=await dispatcher.DispatchBatchAsync(transport,"server-new",20,CancellationToken.None);
    var row=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(summary.ReconciliationRequired==1,"server change blocks old backlog");
    Check(row?.DeliveryState==ReportDeliveryState.ReconciliationRequired,"old backlog held for reconciliation");
    Check(transport.ReportRequests.Count==0,"old backlog never sent to new server");
}

async Task ExpectThrowsAsync(Func<Task> action,string name)
{
    assertions.Add(name);
    try{await action();failures.Add(name);}catch{}
}

async Task WriteResult(string status,int exitCode,string? error)
{
    var payload=new
    {
        case_id=caseId.ToUpperInvariant(),
        status,
        source_sha=ResolveSourceSha(),
        run_started_at=started.ToString("O"),
        run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}",
        exit_code=exitCode,
        assertions,
        failed_assertions=failures,
        raw_log=Path.GetFileName(logPath),
        error
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
}

static Dictionary<string,string> ParseArgs(string[] values)
{
    var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    for(var i=0;i<values.Length;i++)
    {
        if(!values[i].StartsWith("--",StringComparison.Ordinal))continue;
        var key=values[i][2..];
        var value=i+1<values.Length&&!values[i+1].StartsWith("--",StringComparison.Ordinal)?values[++i]:"true";
        result[key]=value;
    }
    return result;
}

static string ResolveSourceSha()
{
    var env=Environment.GetEnvironmentVariable("GITHUB_SHA");
    if(!string.IsNullOrWhiteSpace(env))return env;
    try
    {
        using var process=Process.Start(new ProcessStartInfo("git","rev-parse HEAD"){RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true});
        if(process is null)return "unknown";
        var text=process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(5000);
        return process.ExitCode==0&&!string.IsNullOrWhiteSpace(text)?text:"unknown";
    }
    catch{return "unknown";}
}

sealed class TestEnvironment:IDisposable
{
    private readonly string _root;
    public string DatabasePath{get;}
    public TestLeaseProtector Protector{get;}=new();
    public LocalQueueStore Store{get;}
    public AgentLog Log{get;}
    private TestEnvironment(string root)
    {
        _root=root;
        DatabasePath=Path.Combine(root,"queue.db");
        Store=new LocalQueueStore(DatabasePath,Protector);
        Log=new AgentLog(Path.Combine(root,"logs"));
    }
    public static async Task<TestEnvironment> CreateAsync(string suffix)
    {
        var env=new TestEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-acceptance-{suffix}-{Guid.NewGuid():N}"));
        await env.Store.InitializeAsync();
        return env;
    }
    public ClaimItem MakeClaim(long attemptId,int attemptNo,string destinationKey="prep")
    {
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Acceptance\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        return new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,attemptNo,"lease-test",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            new DestinationConfig(destinationKey,"Test","Test Queue",80,72,1,"combined"));
    }
    public async Task<LocalJob> CreateJobAsync(long attemptId,string scope,string receipt)
    {
        var claim=MakeClaim(attemptId,(int)(attemptId%1000));
        return await Store.PersistReservedAsync(claim,receipt,scope);
    }
    public ReportRequestEnvelope Report(LocalJob job,string requestId,string status,string? spooler,bool retryable,string? code,string? message)
        =>Report(job,requestId,AgentVersionInfo.Current,status,spooler,retryable,code,message);
    public ReportRequestEnvelope Report(LocalJob job,string requestId,string version,string status,string? spooler,bool retryable,string? code,string? message)
        =>new(requestId,version,4,job.AttemptId,job.LocalReceiptId,status,spooler,retryable,code,message);
    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}

enum FakeReportMode{Success,Unauthorized,CommitThenDisconnect,Unprocessable}

sealed class ScriptedTransport:IPrintTransport
{
    private readonly Queue<FakeReportMode> _modes;
    public List<ReportRequestEnvelope> ReportRequests{get;}=[];
    public int PrintInvocationCount{get;private set;}
    public ScriptedTransport(IEnumerable<FakeReportMode> modes)=>_modes=new Queue<FakeReportMode>(modes);
    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)
    {
        ReportRequests.Add(request);
        var mode=_modes.Count>0?_modes.Dequeue():FakeReportMode.Success;
        return mode switch
        {
            FakeReportMode.Success=>Task.FromResult(new ApiResult(true,Status:request.Status,AttemptId:job.AttemptId,JobId:job.ServerJobId,LocalReceiptId:job.LocalReceiptId)),
            FakeReportMode.Unauthorized=>Task.FromException<ApiResult>(new PrintApiException(HttpStatusCode.Unauthorized,"invalid token","invalid_token")),
            FakeReportMode.CommitThenDisconnect=>Task.FromException<ApiResult>(new HttpRequestException("connection closed after server commit")),
            FakeReportMode.Unprocessable=>Task.FromException<ApiResult>(new PrintApiException(HttpStatusCode.UnprocessableEntity,"schema mismatch","schema_mismatch")),
            _=>throw new InvalidOperationException()
        };
    }
    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ClaimResponse(true,request.RequestId,[],DateTimeOffset.UtcNow.ToString("O"),true));
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,AttemptId:item.Attempt.Id,JobId:item.Job.Id,LocalReceiptId:localReceiptId));
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,AttemptId:item.Attempt.Id,JobId:item.Job.Id));
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>Task.FromResult(new AttemptStatusResult(true,job.AttemptId,job.ServerJobId,"claimed","claimed",true,"continue",false,false,job.LeaseExpiresAt.ToString("O"),DateTimeOffset.UtcNow.ToString("O")));
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct){PrintInvocationCount++;return Task.FromResult(new ApiResult(true,Status:"started",AttemptId:job.AttemptId,JobId:job.ServerJobId));}
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>Task.FromResult(new ProbeResponse(true,4,"6.0.0",AgentVersionInfo.Current,[],["attempt_status"],"server-a",DateTimeOffset.UtcNow.ToString("O")));
}
