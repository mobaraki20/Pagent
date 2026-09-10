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
    Console.Error.WriteLine("Usage: --case A25|A26|A27 --results <directory>");
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
    Check(OperatingSystem.IsWindows(),"Windows fault acceptance executes on Windows");
    _=PrepareFixtureWorker();
    switch(caseId.ToUpperInvariant())
    {
        case "A25": await RunA25(); break;
        case "A26": await RunA26(); break;
        case "A27": await RunA27(); break;
        default:
            await WriteResult("NOT_RUN",3,$"Windows fault acceptance case {caseId} is not implemented.");
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
finally
{
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE",null);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",null);
}

async Task RunA25()
{
    using var env=await WindowsServiceTestEnvironment.CreateAsync("a25-pre-fence-kill");
    var job=await env.CreateClaimedJobAsync(3225,"receipt-a25");
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE","hang_before_fence");
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",env.PidPath);
    var service=env.CreateService(transport,new SystemWorkerProcessFactory());

    await InvokePrivateAsync(service,"ProcessOneAsync",CancellationToken.None);
    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var outbox=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    var pid=ReadPid(env.PidPath);

    Check(pid>0,"real child process published its PID before timeout");
    Check(!IsProcessAlive(pid),"production supervisor killed the real child and proved exit before outcome classification");
    Check(outcome is {Status:PrintOutcomeStatus.Failed,Retryable:true},"proven pre-fence termination is durable safe failure");
    Check(outcome?.ErrorCode=="worker_timeout_before_fence","pre-fence timeout has stable diagnostic code");
    Check(outbox is {DeliveryState:ReportDeliveryState.Pending},"pre-fence failure report is committed durably before cleanup");
    Check(!File.Exists(env.FencePath(job)),"pre-fence fixture never created a submission fence");
    Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"A25 traverses production start transport exactly once");
}

async Task RunA26()
{
    using var env=await WindowsServiceTestEnvironment.CreateAsync("a26-post-fence-kill");
    var job=await env.CreateClaimedJobAsync(3226,"receipt-a26");
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE","fence_then_hang");
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",env.PidPath);
    var service=env.CreateService(transport,new SystemWorkerProcessFactory());

    await InvokePrivateAsync(service,"ProcessOneAsync",CancellationToken.None);
    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var outbox=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    var pid=ReadPid(env.PidPath);

    Check(pid>0&&!IsProcessAlive(pid),"real post-fence child is dead before recovery decision");
    Check(outcome is {Status:PrintOutcomeStatus.RecoveryHold,Retryable:false},"post-fence timeout is durable hold rather than printable retry");
    Check(outcome?.ErrorCode=="worker_timeout_after_fence","post-fence ambiguity has stable diagnostic code");
    Check(outbox is {DeliveryState:ReportDeliveryState.Pending},"post-fence hold report is durable");
    Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"post-fence timeout never repeats start during same pass");

    var restartedStore=await env.CreateRestartedStoreAsync();
    var noStartFactory=new CountingNoStartFactory();
    var restarted=env.CreateService(transport,noStartFactory,restartedStore);
    await InvokePrivateAsync(restarted,"RecoverAsync",CancellationToken.None);
    await InvokePrivateAsync(restarted,"ProcessOneAsync",CancellationToken.None);
    var restartedOutcome=await restartedStore.GetOutcomeAsync(job.AttemptId);
    Check(restartedOutcome is {Status:PrintOutcomeStatus.RecoveryHold,Retryable:false},"restart preserves authoritative local ambiguity outcome");
    Check(noStartFactory.StartCount==0,"restart after post-fence ambiguity performs zero automatic submission invocations");
    Check(server.Requests.Count==1,"restart after post-fence ambiguity performs no second start request");
}

async Task RunA27()
{
    using var env=await WindowsServiceTestEnvironment.CreateAsync("a27-durable-result-wins");
    var job=await env.CreateClaimedJobAsync(3227,"receipt-a27");
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE","submitted_result_then_crash");
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",env.PidPath);
    var service=env.CreateService(transport,new SystemWorkerProcessFactory());

    await InvokePrivateAsync(service,"ProcessOneAsync",CancellationToken.None);
    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var outbox=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    var pid=ReadPid(env.PidPath);

    Check(pid>0&&!IsProcessAlive(pid),"fixture child exited nonzero and no process remains");
    Check(outcome?.Status==PrintOutcomeStatus.Submitted,"valid durable submitted result wins over nonzero child exit code");
    Check(!string.IsNullOrWhiteSpace(outcome?.SpoolerJobId)&&outcome!.SpoolerJobId!.StartsWith("fixture-spool-",StringComparison.Ordinal),"durable spooler evidence is preserved");
    Check(outbox is {DeliveryState:ReportDeliveryState.Pending},"submitted outcome and report outbox are committed together before evidence cleanup");
    Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"A27 performs one production start request only");

    var restartedStore=await env.CreateRestartedStoreAsync();
    var noStartFactory=new CountingNoStartFactory();
    var restarted=env.CreateService(transport,noStartFactory,restartedStore);
    await InvokePrivateAsync(restarted,"RecoverAsync",CancellationToken.None);
    await InvokePrivateAsync(restarted,"ProcessOneAsync",CancellationToken.None);
    Check((await restartedStore.GetOutcomeAsync(job.AttemptId))?.Status==PrintOutcomeStatus.Submitted,"restart preserves durable submitted result despite prior crash exit");
    Check(noStartFactory.StartCount==0,"restart replays no print after a durable submitted worker result");
    Check(server.Requests.Count==1,"restart replays no start after durable submitted result");
}

static string PrepareFixtureWorker()
{
    var agentRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..",".."));
    var fixtureBin=Path.Combine(agentRoot,"tests","Sokna.PrintAgent.SubprocessFixture","bin");
    var fixtureExe=Directory.Exists(fixtureBin)
        ?Directory.EnumerateFiles(fixtureBin,"Sokna.PrintAgent.SubprocessFixture.exe",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        :null;
    if(string.IsNullOrWhiteSpace(fixtureExe))
        throw new FileNotFoundException("Subprocess fixture executable was not built before Windows fault acceptance.");

    var sourceDir=Path.GetDirectoryName(fixtureExe)!;
    var workerDir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker"));
    Directory.CreateDirectory(workerDir);
    foreach(var file in Directory.EnumerateFiles(sourceDir))
        File.Copy(file,Path.Combine(workerDir,Path.GetFileName(file)),true);
    var expectedWorker=Path.Combine(workerDir,"Sokna.PrintAgent.Worker.exe");
    File.Copy(fixtureExe,expectedWorker,true);
    return expectedWorker;
}

static int ReadPid(string path)
{
    if(!File.Exists(path))return 0;
    return int.TryParse(File.ReadAllText(path).Trim(),out var pid)?pid:0;
}

static bool IsProcessAlive(int pid)
{
    if(pid<=0)return false;
    try
    {
        using var process=Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch(ArgumentException){return false;}
    catch(InvalidOperationException){return false;}
}

static bool IsStart(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase);

static async Task InvokePrivateAsync(PrintAgentService service,string methodName,CancellationToken ct)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    var task=method.Invoke(service,[ct]) as Task
        ??throw new InvalidOperationException($"{methodName} did not return Task.");
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

sealed class WindowsServiceTestEnvironment:IDisposable
{
    private readonly string _root;
    public AgentPaths Paths{get;}
    public DpapiLeaseTokenProtector Protector{get;}=new();
    public LocalQueueStore Store{get;}
    public AgentLog Log{get;}
    public string PidPath=>Path.Combine(_root,"fixture.pid");

    private WindowsServiceTestEnvironment(string root)
    {
        _root=root;
        Paths=new AgentPaths(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
        Store=new LocalQueueStore(Paths.DatabasePath,Protector);
        Log=new AgentLog(Paths.LogsPath);
    }

    public static async Task<WindowsServiceTestEnvironment> CreateAsync(string suffix)
    {
        var env=new WindowsServiceTestEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-windows-fault-{suffix}-{Guid.NewGuid():N}"));
        await env.Store.InitializeAsync();
        return env;
    }

    public async Task<LocalQueueStore> CreateRestartedStoreAsync()
    {
        var store=new LocalQueueStore(Paths.DatabasePath,Protector);
        await store.InitializeAsync();
        return store;
    }

    public async Task<LocalJob> CreateClaimedJobAsync(long attemptId,string receipt)
    {
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Windows Fault Acceptance\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        var claim=new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-windows-fault",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            new DestinationConfig("prep","Test","Test Queue",80,72,1,"combined"));
        var job=await Store.PersistReservedAsync(claim,receipt,"server-a");
        await Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
        return (await Store.GetByAttemptAsync(job.AttemptId))!;
    }

    public string FencePath(LocalJob job)=>Path.Combine(Paths.WorkPath,$"fence-{job.ServerJobId}-{job.AttemptId}.mark");

    public PrintAgentService CreateService(IPrintTransport transport,IWorkerProcessFactory factory,LocalQueueStore? store=null)
    {
        store??=Store;
        var dispatcher=new ReportDispatcher(store,new ReportDeliveryPolicy(jitter:()=>0.5),Log);
        var service=new PrintAgentService(
            Paths,store,new ReadyPrinterHealthReader(),NullLogger<PrintAgentService>.Instance,Log,new PrintWakeSignal(),dispatcher,new DurableMutationRequestStore(store),new BridgeRuntimeState(),new WorkerSupervisor(factory));
        SetField(service,"_api",transport);
        SetField(service,"_attemptStatusSupported",true);
        SetField(service,"_serverScope","server-a");
        SetField(service,"_boundServerScope","server-a");
        SetField(service,"_options",new AgentOptions
        {
            WorkerTimeoutSeconds=1,
            WorkerExitProofTimeoutMilliseconds=1000,
            WorkerShutdownExitProofTimeoutMilliseconds=1000
        });
        return service;
    }

    private static void SetField(object target,string name,object value)
    {
        var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)
            ??throw new MissingFieldException(target.GetType().FullName,name);
        field.SetValue(target,value);
    }

    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

sealed class ReadyPrinterHealthReader:IPrinterHealthReader
{
    private static readonly IReadOnlyList<PrinterQueueHealth> Queues=[new("Test Queue",false,false,false,false,0,"Acceptance Driver","LPT1:")];
    public PrinterHealthSnapshot Read(TimeSpan freshnessWindow)=>new(Queues,DateTimeOffset.UtcNow,null,null,0,true,1);
}

sealed class CountingNoStartFactory:IWorkerProcessFactory
{
    public int StartCount{get;private set;}
    public IWorkerProcess Start(WorkerLaunchSpec spec)
    {
        StartCount++;
        throw new InvalidOperationException("worker launch must not occur during recovery assertion");
    }
}
