using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sokna.PrintAgent.Acceptance;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||string.IsNullOrWhiteSpace(caseId) ||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A13|A14 --results <directory>");
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
        case "A13": await RunA13(); break;
        case "A14": await RunA14(); break;
        default:
            await WriteResult("NOT_RUN",3,$"Service acceptance case {caseId} is not implemented.");
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

async Task RunA13()
{
    using(var env=await ServiceTestEnvironment.CreateAsync("a13-authoritative"))
    {
        var job=await env.CreateJobAsync(2613,"receipt-a13-authoritative",DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.AttemptStatusExpired]);
        using var http=new HttpClient();
        var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
        var processFactory=new CountingNoStartFactory();
        var service=env.CreateService(transport,true,processFactory);
        await InvokePrivateAsync(service,"AcceptAnyReservedAsync");

        var after=await env.Store.GetByAttemptAsync(job.AttemptId);
        Check(after?.State==LocalJobState.Resolved,"authoritative expired state closes reservation");
        Check(processFactory.StartCount==0,"authoritative expiry never launches worker");
        Check(server.Requests.Count==1&&IsAttemptStatus(server.Requests[0]),"expired reservation is reconciled through real attempt_status HTTP request");
        Check(server.Requests.All(r=>!IsStart(r)),"expiry reconciliation never calls start");
    }

    using(var env=await ServiceTestEnvironment.CreateAsync("a13-disconnect"))
    {
        var job=await env.CreateJobAsync(2713,"receipt-a13-disconnect",DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.DisconnectAfterCommit]);
        using var http=new HttpClient();
        var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
        var processFactory=new CountingNoStartFactory();
        var service=env.CreateService(transport,true,processFactory);
        await InvokePrivateAsync(service,"AcceptAnyReservedAsync");

        var after=await env.Store.GetByAttemptAsync(job.AttemptId);
        Check(after?.State==LocalJobState.Reserved,"network loss during authoritative lookup never converts reservation to Resolved");
        Check(processFactory.StartCount==0,"network loss never launches worker");
        Check(server.Requests.Count==1&&IsAttemptStatus(server.Requests[0]),"network-loss case traverses production attempt_status transport");
    }
}

async Task RunA14()
{
    var cases=new (string Name,LoopbackResponseKind Response,string ExpectedCode)[]
    {
        ("success-false",LoopbackResponseKind.AttemptStatusSuccessFalse,"attempt_status_business_failed"),
        ("receipt-mismatch",LoopbackResponseKind.AttemptStatusReceiptMismatch,"attempt_status_receipt_mismatch"),
        ("identity-mismatch",LoopbackResponseKind.AttemptStatusIdentityMismatch,"attempt_status_attempt_mismatch"),
        ("human-resolution",LoopbackResponseKind.AttemptStatusHumanResolution,"attempt_status_human_resolution"),
        ("bad-next-action",LoopbackResponseKind.AttemptStatusBadAction,"attempt_status_next_action_mismatch"),
        ("unknown-state",LoopbackResponseKind.AttemptStatusUnknownState,"attempt_status_unknown_state"),
        ("offsetless-time",LoopbackResponseKind.AttemptStatusOffsetless,"timestamp_offset_required")
    };

    var sequence=0;
    foreach(var scenario in cases)
    {
        using var env=await ServiceTestEnvironment.CreateAsync("a14-"+scenario.Name);
        var attemptId=28140+(++sequence);
        var job=await env.CreateJobAsync(attemptId,"receipt-a14-"+scenario.Name,DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var server=new LoopbackPrintApiServer([scenario.Response]);
        using var http=new HttpClient();
        var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
        var processFactory=new CountingNoStartFactory();
        var service=env.CreateService(transport,true,processFactory);

        await InvokePrivateAsync(service,"AcceptAnyReservedAsync");
        var after=await env.Store.GetByAttemptAsync(job.AttemptId);
        Check(after?.State==LocalJobState.Reserved,$"{scenario.Name}: invalid status grants no local continuation");
        Check(processFactory.StartCount==0,$"{scenario.Name}: invalid status launches no worker");
        Check(server.Requests.Count==1&&IsAttemptStatus(server.Requests[0]),$"{scenario.Name}: production service used real attempt_status transport");
        Check(server.Requests.All(r=>!IsStart(r)),$"{scenario.Name}: invalid status never reaches start endpoint");

        // Verify the stable protocol diagnostic at the transport boundary as well. The service intentionally
        // treats this as unresolved and keeps the reservation instead of inventing an outcome.
        await using var diagnosticServer=new LoopbackPrintApiServer([scenario.Response]);
        using var diagnosticHttp=new HttpClient();
        var diagnosticTransport=new HttpPrintTransport(diagnosticHttp,diagnosticServer.BaseUrl,"acceptance-token",env.Protector);
        string? observedCode=null;
        try{_ = await diagnosticTransport.AttemptStatusAsync(job,CancellationToken.None);}
        catch(PrintProtocolException e){observedCode=e.Code;}
        Check(observedCode==scenario.ExpectedCode,$"{scenario.Name}: typed protocol code is {scenario.ExpectedCode}");
    }
}

static bool IsAttemptStatus(CapturedHttpRequest request)
    => request.Method=="POST"&&request.Target.Contains("action=attempt_status",StringComparison.OrdinalIgnoreCase);
static bool IsStart(CapturedHttpRequest request)
    => request.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase);

static async Task InvokePrivateAsync(PrintAgentService service,string methodName)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    var task=method.Invoke(service,[CancellationToken.None]) as Task
        ?? throw new InvalidOperationException($"{methodName} did not return Task.");
    await task;
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

sealed class ServiceTestEnvironment:IDisposable
{
    private readonly string _root;
    public AgentPaths Paths{get;}
    public string DatabasePath=>Paths.DatabasePath;
    public TestLeaseProtector Protector{get;}=new();
    public LocalQueueStore Store{get;}
    public AgentLog Log{get;}

    private ServiceTestEnvironment(string root)
    {
        _root=root;
        Paths=new AgentPaths(
            root,
            Path.Combine(root,"config.json"),
            Path.Combine(root,"secret.dat"),
            Path.Combine(root,"queue.db"),
            Path.Combine(root,"logs"),
            Path.Combine(root,"work"),
            Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
        Store=new LocalQueueStore(Paths.DatabasePath,Protector);
        Log=new AgentLog(Paths.LogsPath);
    }

    public static async Task<ServiceTestEnvironment> CreateAsync(string suffix)
    {
        var env=new ServiceTestEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-service-acceptance-{suffix}-{Guid.NewGuid():N}"));
        await env.Store.InitializeAsync();
        return env;
    }

    public async Task<LocalJob> CreateJobAsync(long attemptId,string receipt,DateTimeOffset leaseExpiresAt)
    {
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Service Acceptance\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        var claim=new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-test",leaseExpiresAt.ToString("O")),
            new DestinationConfig("prep","Test","Test Queue",80,72,1,"combined"));
        return await Store.PersistReservedAsync(claim,receipt,"server-a");
    }

    public PrintAgentService CreateService(IPrintTransport transport,bool attemptStatusSupported,CountingNoStartFactory processFactory)
    {
        var dispatcher=new ReportDispatcher(Store,new ReportDeliveryPolicy(jitter:()=>0.5),Log);
        var service=new PrintAgentService(
            Paths,
            Store,
            new ReadyPrinterHealthProvider(),
            NullLogger<PrintAgentService>.Instance,
            Log,
            new PrintWakeSignal(),
            dispatcher,
            new DurableMutationRequestStore(Store),
            new WorkerSupervisor(processFactory));
        SetPrivateField(service,"_api",transport);
        SetPrivateField(service,"_attemptStatusSupported",attemptStatusSupported);
        SetPrivateField(service,"_serverScope","server-a");
        SetPrivateField(service,"_boundServerScope","server-a");
        return service;
    }

    private static void SetPrivateField(object instance,string name,object value)
    {
        var field=instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName,name);
        field.SetValue(instance,value);
    }

    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

sealed class ReadyPrinterHealthProvider:IPrinterHealthProvider
{
    public IReadOnlyList<PrinterQueueHealth> GetQueues()=>[new("Test Queue",false,false,false,false,0,"Acceptance Driver","LPT1:")];
}

sealed class CountingNoStartFactory:IWorkerProcessFactory
{
    public int StartCount{get;private set;}
    public IWorkerProcess Start(WorkerLaunchSpec spec)
    {
        StartCount++;
        throw new InvalidOperationException("Worker launch is forbidden in this acceptance scenario.");
    }
}

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}
