using System.Diagnostics;
using System.Text.Json;
using Sokna.PrintAgent.Acceptance;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||string.IsNullOrWhiteSpace(caseId) ||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A04|A06|A17 --results <directory>");
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
        case "A04": await RunA04(); break;
        case "A06": await RunA06(); break;
        case "A17": await RunA17(); break;
        default:
            await WriteResult("NOT_RUN",3,$"Transport acceptance case {caseId} is not implemented.");
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

async Task RunA04()
{
    using var env=await TestEnvironment.CreateAsync("a04-http");
    var job=await env.CreateJobAsync(2404,"server-a","receipt-a04-http");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_offline","offline","worker:pre-fence");
    var request=env.Report(job,"a04-http-report","failed",null,true,"printer_offline","offline");
    var outbox=await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var durableBody=outbox.BodyJson;

    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.DisconnectAfterCommit,LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);

    var first=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var afterDisconnect=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(first.Backoff==1,"lost response after request commit enters backoff");
    Check(afterDisconnect?.DeliveryState==ReportDeliveryState.Backoff,"durable outbox survives disconnect");
    Check(server.Requests.Count==1,"server captured exactly one committed request before disconnect");
    Check(IsReport(server.Requests[0]),"real HttpPrintTransport called report endpoint");
    Check(server.Requests[0].Authorization=="Bearer acceptance-token","production bearer header reached loopback server");

    await env.Store.MarkReportDeliveryAsync(afterDisconnect!.Id,ReportDeliveryState.Pending,"advance acceptance retry clock",null,"test_retry",DateTimeOffset.UtcNow.AddSeconds(-1));
    var second=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var delivered=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(second.Delivered==1,"same durable report succeeds on replay");
    Check(delivered?.DeliveryState==ReportDeliveryState.Delivered,"replayed report is marked delivered only after ACK");
    Check(server.Requests.Count==2,"loopback server observed exactly two HTTP report requests");
    Check(IsReport(server.Requests[1]),"replay stays on report endpoint and never invokes print start");
    Check(server.Requests[0].Body==server.Requests[1].Body,"wire request body is byte-for-byte stable across lost ACK replay");

    using var wire=JsonDocument.Parse(server.Requests[0].Body);
    Check(wire.RootElement.GetProperty("request_id").GetString()==request.RequestId,"wire request preserves durable request_id");
    Check(wire.RootElement.GetProperty("attempt_id").GetInt64()==job.AttemptId,"wire request preserves attempt identity");
    Check(wire.RootElement.GetProperty("local_receipt_id").GetString()==job.LocalReceiptId,"wire request preserves local receipt identity");
    Check((await env.Store.GetOutboxForAttemptAsync(job.AttemptId))?.BodyJson==durableBody,"stored report envelope is never regenerated during retry");
    Check(server.Requests.All(r=>!r.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase)),"report delivery retry never calls start/print path");
}

async Task RunA06()
{
    await using var server=new LoopbackPrintApiServer([
        LoopbackResponseKind.Unauthorized,
        LoopbackResponseKind.ProbeSuccess,
        LoopbackResponseKind.Success]);
    var scope=ServerScopeResolver.Resolve(server.BaseUrl,"acceptance-server");
    using var env=await TestEnvironment.CreateAsync("a06-http");
    var job=await env.CreateJobAsync(2406,scope,"receipt-a06-http");
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_offline","offline","worker:pre-fence");
    var request=env.Report(job,"a06-http-report","failed",null,true,"printer_offline","offline");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);

    using(var badHttp=new HttpClient())
    {
        var badTransport=new HttpPrintTransport(badHttp,server.BaseUrl,"expired-token",env.Protector);
        var first=await dispatcher.DispatchBatchAsync(badTransport,scope,20,CancellationToken.None);
        var blocked=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
        Check(first.AuthBlocked==1,"real HTTP 401 blocks report delivery");
        Check(blocked?.DeliveryState==ReportDeliveryState.AuthBlocked,"401 state is durable across loops");
    }

    using(var repairedHttp=new HttpClient())
    {
        var repairedTransport=new HttpPrintTransport(repairedHttp,server.BaseUrl,"repaired-token",env.Protector);
        var probe=await repairedTransport.ProbeAsync(CancellationToken.None);
        var probedScope=ServerScopeResolver.Resolve(server.BaseUrl,probe.ServerInstanceId);
        Check(probe.Success&&probe.ProtocolVersion==4,"repaired credential performs successful real probe");
        Check(probedScope==scope,"credential probe confirms the same server identity before unblock");
        var resumed=await dispatcher.ResumeAfterCredentialProbeAsync(probedScope,CancellationToken.None);
        Check(resumed==1,"successful probe unblocks only the matching server scope");
        var second=await dispatcher.DispatchBatchAsync(repairedTransport,scope,20,CancellationToken.None);
        var delivered=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
        Check(second.Delivered==1,"same durable report is delivered after credential repair");
        Check(delivered?.DeliveryState==ReportDeliveryState.Delivered,"repaired credential ACK closes delivery");
    }

    Check(server.Requests.Count==3,"server observed report, probe, then report");
    Check(IsReport(server.Requests[0])&&IsProbe(server.Requests[1])&&IsReport(server.Requests[2]),"HTTP sequence is report -> probe -> same report");
    Check(server.Requests[0].Authorization=="Bearer expired-token","first report used old credential");
    Check(server.Requests[1].Authorization=="Bearer repaired-token"&&server.Requests[2].Authorization=="Bearer repaired-token","probe and replay use repaired credential");
    Check(server.Requests[0].Body==server.Requests[2].Body,"credential repair preserves request_id and report body exactly");
    Check(server.Requests.All(r=>!r.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase)),"credential repair never reprints");
}

async Task RunA17()
{
    await VerifyReportResponseFaultAsync(
        "business-false",
        24171,
        LoopbackResponseKind.BusinessFailure,
        "destination_forbidden",
        "success=false in HTTP 200 is reconciliation, never success");
    await VerifyReportResponseFaultAsync(
        "malformed-json",
        24172,
        LoopbackResponseKind.MalformedJson,
        "invalid_success_json",
        "malformed HTTP 200 JSON is a protocol reconciliation fault");
    await VerifyReportResponseFaultAsync(
        "invalid-types",
        24173,
        LoopbackResponseKind.InvalidTypes,
        "invalid_success_json",
        "wrong JSON field types are protocol reconciliation faults");
    await VerifyReportResponseFaultAsync(
        "identity-mismatch",
        24174,
        LoopbackResponseKind.MismatchedReport,
        "report_attempt_identity_mismatch",
        "mismatched HTTP 200 identity is semantic reconciliation, not transient retry");
}

async Task VerifyReportResponseFaultAsync(string suffix,long attemptId,LoopbackResponseKind responseKind,string expectedCode,string assertionPrefix)
{
    using var env=await TestEnvironment.CreateAsync("a17-"+suffix);
    var job=await env.CreateJobAsync(attemptId,"server-a","receipt-a17-"+suffix);
    var draft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,false,"invalid_payload","invalid","worker:pre-fence");
    var request=env.Report(job,"a17-"+suffix+"-report","failed",null,false,"invalid_payload","invalid");
    await env.Store.CommitOutcomeAndReportAsync(job,draft,request);

    await using var server=new LoopbackPrintApiServer([responseKind]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    var dispatcher=new ReportDispatcher(env.Store,new ReportDeliveryPolicy(jitter:()=>0.5),env.Log);
    var summary=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
    var row=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);

    Check(summary.ReconciliationRequired==1,assertionPrefix);
    Check(summary.Backoff==0,$"{suffix}: contract/semantic fault does not enter network backoff");
    Check(summary.Delivered==0,$"{suffix}: HTTP status alone never creates delivered state");
    Check(summary.LastErrorCode==expectedCode,$"{suffix}: stable diagnostic code is {expectedCode}");
    Check(row?.DeliveryState==ReportDeliveryState.ReconciliationRequired,$"{suffix}: invalid ACK is durably quarantined");
    Check(outcome is {Status:PrintOutcomeStatus.Failed,Retryable:false},$"{suffix}: invalid server response never mutates authoritative print outcome");
    Check(server.Requests.Count==1&&IsReport(server.Requests[0]),$"{suffix}: test traverses real HttpPrintTransport request");
    Check(server.Requests.All(r=>!r.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase)),$"{suffix}: reconciliation never invokes start/print");
}

static bool IsReport(CapturedHttpRequest request)
    => request.Method=="POST"&&request.Target.Contains("action=report",StringComparison.OrdinalIgnoreCase);

static bool IsProbe(CapturedHttpRequest request)
    => request.Method=="POST"&&request.Target.Contains("action=probe",StringComparison.OrdinalIgnoreCase);

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
        using var process=Process.Start(new ProcessStartInfo("git","rev-parse HEAD")
        {
            RedirectStandardOutput=true,
            UseShellExecute=false,
            CreateNoWindow=true
        });
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
        var env=new TestEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-transport-acceptance-{suffix}-{Guid.NewGuid():N}"));
        await env.Store.InitializeAsync();
        return env;
    }

    public async Task<LocalJob> CreateJobAsync(long attemptId,string scope,string receipt)
    {
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Transport Acceptance\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        var claim=new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-test",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            new DestinationConfig("prep","Test","Test Queue",80,72,1,"combined"));
        return await Store.PersistReservedAsync(claim,receipt,scope);
    }

    public ReportRequestEnvelope Report(LocalJob job,string requestId,string status,string? spooler,bool retryable,string? code,string? message)
        =>new(requestId,AgentVersionInfo.Current,4,job.AttemptId,job.LocalReceiptId,status,spooler,retryable,code,message);

    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}
