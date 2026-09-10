using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sokna.PrintAgent.Acceptance;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A29",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A29 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A29.log");
var resultPath=Path.Combine(resultsDirectory,"A29.result.json");
var assertions=new List<string>();
var failures=new List<string>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    Check(OperatingSystem.IsWindows(),"A29 executes on Windows so locked-file deletion and process shutdown semantics are real");
    _=PrepareFixtureWorker();
    await ShutdownBeforeFenceAsync();
    await ShutdownAfterFenceAsync();
    await ShutdownDuringReportAsync();
    await CleanupRetryAndRetentionAsync();

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }

    Log($"PASS A29; assertions={assertions.Count}");
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

async Task ShutdownBeforeFenceAsync()
{
    using var env=await TestEnvironment.CreateAsync("shutdown-pre-fence");
    var job=await env.CreateClaimedJobAsync(34291,"receipt-a29-pre");
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE","hang_before_fence");
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",env.PidPath);
    var service=env.CreateService(transport,new SystemWorkerProcessFactory(),workerTimeoutSeconds:20);
    using var stop=new CancellationTokenSource();

    var processing=InvokePrivateAsync(service,"ProcessOneAsync",stop.Token);
    await WaitForFileAsync(env.StartSignalPath(job),TimeSpan.FromSeconds(8));
    var pid=await WaitForPidAsync(env.PidPath,TimeSpan.FromSeconds(8));
    stop.Cancel();
    await processing;

    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var outbox=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(pid>0&&!IsProcessAlive(pid),"shutdown during pre-fence render kills the real guarded child and proves exit");
    Check(outcome is {Status:PrintOutcomeStatus.Failed,Retryable:true},"shutdown before fence commits durable safe failure");
    Check(outcome?.ErrorCode=="worker_service_shutdown_before_fence","pre-fence shutdown has stable service-shutdown diagnostic");
    Check(outbox is {DeliveryState:ReportDeliveryState.Pending},"pre-fence shutdown commits outcome and outbox before cleanup");
    Check(!File.Exists(env.FencePath(job)),"pre-fence shutdown never invents a submission fence");
    Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"pre-fence shutdown traverses production Start exactly once");
}

async Task ShutdownAfterFenceAsync()
{
    using var env=await TestEnvironment.CreateAsync("shutdown-post-fence");
    var job=await env.CreateClaimedJobAsync(34292,"receipt-a29-post");
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_MODE","fence_then_hang");
    Environment.SetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH",env.PidPath);
    var service=env.CreateService(transport,new SystemWorkerProcessFactory(),workerTimeoutSeconds:20);
    using var stop=new CancellationTokenSource();

    var processing=InvokePrivateAsync(service,"ProcessOneAsync",stop.Token);
    await WaitForFileAsync(env.FencePath(job),TimeSpan.FromSeconds(8));
    var pid=await WaitForPidAsync(env.PidPath,TimeSpan.FromSeconds(8));
    stop.Cancel();
    await processing;

    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var outbox=await env.Store.GetOutboxForAttemptAsync(job.AttemptId);
    Check(pid>0&&!IsProcessAlive(pid),"shutdown after durable fence kills the real guarded child and proves exit");
    Check(outcome is {Status:PrintOutcomeStatus.RecoveryHold,Retryable:false},"shutdown after fence commits durable ambiguity instead of printable retry");
    Check(outcome?.ErrorCode=="worker_service_shutdown_after_fence","post-fence shutdown has stable service-shutdown diagnostic");
    Check(outbox is {DeliveryState:ReportDeliveryState.Pending},"post-fence shutdown commits hold report durably");

    var restartedStore=await env.CreateRestartedStoreAsync();
    var noStart=new CountingNoStartFactory();
    var restarted=env.CreateService(transport,noStart,restartedStore,20);
    await InvokePrivateAsync(restarted,"RecoverAsync",CancellationToken.None);
    await InvokePrivateAsync(restarted,"ProcessOneAsync",CancellationToken.None);
    Check((await restartedStore.GetOutcomeAsync(job.AttemptId)) is {Status:PrintOutcomeStatus.RecoveryHold,Retryable:false},"restart preserves post-fence shutdown ambiguity");
    Check(noStart.StartCount==0&&server.Requests.Count==1,"restart after post-fence shutdown performs zero automatic resubmission");
}

async Task ShutdownDuringReportAsync()
{
    using var env=await TestEnvironment.CreateAsync("shutdown-report");
    var job=await env.CreateClaimedJobAsync(34293,"receipt-a29-report");
    await env.CommitOutcomeAsync(job,PrintOutcomeStatus.Failed,true,"a29_report_failure","pre-existing durable failed outcome");
    var before=await env.Store.GetOutboxForAttemptAsync(job.AttemptId)??throw new InvalidOperationException("A29 report outbox missing.");
    var transport=new BlockingReportTransport();
    var noStart=new CountingNoStartFactory();
    var service=env.CreateService(transport,noStart,workerTimeoutSeconds:20);
    using var stop=new CancellationTokenSource();

    InvokePrivateVoid(service,"StartSideIo",stop.Token);
    await transport.ReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    stop.Cancel();
    await transport.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    InvokePrivateVoid(service,"ObserveSideIoResults");

    var outcome=await env.Store.GetOutcomeAsync(job.AttemptId);
    var after=await env.Store.GetOutboxForAttemptAsync(job.AttemptId)??throw new InvalidOperationException("A29 report outbox disappeared.");
    Check(outcome is {Status:PrintOutcomeStatus.Failed,Retryable:true},"service shutdown during report cannot rewrite the durable print outcome");
    Check(after.RequestId==before.RequestId&&after.BodyJson==before.BodyJson,"report cancellation preserves the first durable request identity and body");
    Check(after.DeliveryState!=ReportDeliveryState.Delivered,"cancelled in-flight report is not falsely ACKed");
    Check(noStart.StartCount==0,"report shutdown path launches no Worker");
}

async Task CleanupRetryAndRetentionAsync()
{
    using var env=await TestEnvironment.CreateAsync("cleanup-retry");
    var clear=await env.CreateClaimedJobAsync(34294,"receipt-a29-clear");
    await env.CommitOutcomeAsync(clear,PrintOutcomeStatus.Failed,true,"a29_cleanup_clear","durable failure before cleanup");
    var clearOutcome=await env.Store.GetOutcomeAsync(clear.AttemptId)??throw new InvalidOperationException("Clear outcome missing.");
    var clearEvidence=env.ResultPath(clear);
    await File.WriteAllTextAsync(clearEvidence,"durable-worker-evidence");
    var janitor=new WorkerEvidenceJanitor(env.Paths,env.Store,env.Log,TimeSpan.FromDays(30),TimeSpan.FromMinutes(1));

    using(var lockStream=new FileStream(clearEvidence,FileMode.Open,FileAccess.Read,FileShare.None))
    {
        var locked=await janitor.SweepOnceAsync(clearOutcome.CommittedAt.AddMinutes(1));
        Check(locked.Failed>=1&&File.Exists(clearEvidence),"real Windows locked-file delete failure is retained for a later cleanup retry");
    }

    var retried=await janitor.SweepOnceAsync(clearOutcome.CommittedAt.AddMinutes(2));
    Check(retried.Deleted>=1&&!File.Exists(clearEvidence),"later janitor sweep retries and removes clear-outcome evidence after the lock is released");

    var ambiguous=await env.CreateClaimedJobAsync(34295,"receipt-a29-ambiguous");
    var ambiguousEvidence=env.FencePath(ambiguous);
    await File.WriteAllTextAsync(ambiguousEvidence,"active-fence-evidence");
    var active=await janitor.SweepOnceAsync(DateTimeOffset.UtcNow.AddDays(60));
    Check(active.Retained>=1&&File.Exists(ambiguousEvidence),"evidence without a durable outcome is always retained as potentially active");

    await env.CommitOutcomeAsync(ambiguous,PrintOutcomeStatus.RecoveryHold,false,"a29_ambiguous","post-fence ambiguity");
    var ambiguousOutcome=await env.Store.GetOutcomeAsync(ambiguous.AttemptId)??throw new InvalidOperationException("Ambiguous outcome missing.");
    var beforeRetention=await janitor.SweepOnceAsync(ambiguousOutcome.CommittedAt.AddDays(29));
    Check(beforeRetention.Retained>=1&&File.Exists(ambiguousEvidence),"RecoveryHold evidence remains available throughout the forensic retention window");
    var afterRetention=await janitor.SweepOnceAsync(ambiguousOutcome.CommittedAt.AddDays(31));
    Check(afterRetention.Deleted>=1&&!File.Exists(ambiguousEvidence),"orphaned ambiguous evidence becomes cleanup-eligible only after the documented retention window");
}

static string PrepareFixtureWorker()
{
    var agentRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..",".."));
    var fixtureBin=Path.Combine(agentRoot,"tests","Sokna.PrintAgent.SubprocessFixture","bin");
    var fixtureExe=Directory.Exists(fixtureBin)
        ?Directory.EnumerateFiles(fixtureBin,"Sokna.PrintAgent.SubprocessFixture.exe",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        :null;
    if(string.IsNullOrWhiteSpace(fixtureExe))throw new FileNotFoundException("Subprocess fixture executable was not built before A29.");

    var sourceDir=Path.GetDirectoryName(fixtureExe)!;
    var workerDir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker"));
    Directory.CreateDirectory(workerDir);
    foreach(var file in Directory.EnumerateFiles(sourceDir))File.Copy(file,Path.Combine(workerDir,Path.GetFileName(file)),true);
    var expectedWorker=Path.Combine(workerDir,"Sokna.PrintAgent.Worker.exe");
    File.Copy(fixtureExe,expectedWorker,true);
    return expectedWorker;
}

static async Task WaitForFileAsync(string path,TimeSpan timeout)
{
    var deadline=DateTimeOffset.UtcNow+timeout;
    while(DateTimeOffset.UtcNow<deadline)
    {
        if(File.Exists(path))return;
        await Task.Delay(20);
    }
    throw new TimeoutException($"Expected A29 file was not observed: {Path.GetFileName(path)}");
}

static async Task<int> WaitForPidAsync(string path,TimeSpan timeout)
{
    var deadline=DateTimeOffset.UtcNow+timeout;
    while(DateTimeOffset.UtcNow<deadline)
    {
        if(File.Exists(path)&&int.TryParse(File.ReadAllText(path).Trim(),out var pid)&&pid>0)return pid;
        await Task.Delay(20);
    }
    throw new TimeoutException("A29 worker PID was not observed.");
}

static bool IsProcessAlive(int pid)
{
    try{using var process=Process.GetProcessById(pid);return !process.HasExited;}
    catch(ArgumentException){return false;}
    catch(InvalidOperationException){return false;}
}

static bool IsStart(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase);

static async Task InvokePrivateAsync(PrintAgentService service,string methodName,CancellationToken ct)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    var task=method.Invoke(service,[ct]) as Task??throw new InvalidOperationException($"{methodName} did not return Task.");
    await task;
}

static void InvokePrivateVoid(PrintAgentService service,string methodName,params object?[] args)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    _=method.Invoke(service,args);
}

async Task WriteResult(string status,int exitCode,string? error)
{
    var payload=new
    {
        case_id="A29",
        status,
        source_sha=ResolveSourceSha(),
        run_started_at=started.ToString("O"),
        run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}",
        exit_code=exitCode,
        test_name="A29 shutdown and cleanup through production PrintAgentService, WorkerSupervisor and WorkerEvidenceJanitor",
        test_path="tests/Sokna.PrintAgent.CleanupAcceptance/Program.cs",
        command="dotnet run --project tests/Sokna.PrintAgent.CleanupAcceptance/Sokna.PrintAgent.CleanupAcceptance.csproj -c Release --no-build -- --case A29 --results <dir>",
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
    public AgentPaths Paths{get;}
    public DpapiLeaseTokenProtector Protector{get;}=new();
    public LocalQueueStore Store{get;}
    public AgentLog Log{get;}
    public string PidPath=>Path.Combine(_root,"fixture.pid");

    private TestEnvironment(string root)
    {
        _root=root;
        Paths=new AgentPaths(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
        Store=new LocalQueueStore(Paths.DatabasePath,Protector);
        Log=new AgentLog(Paths.LogsPath);
    }

    public static async Task<TestEnvironment> CreateAsync(string suffix)
    {
        var env=new TestEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-a29-{suffix}-{Guid.NewGuid():N}"));
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
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"A29 Shutdown Cleanup\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        var claim=new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-a29",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            new DestinationConfig("prep","Test","Test Queue",80,72,1,"combined"));
        var job=await Store.PersistReservedAsync(claim,receipt,"server-a");
        await Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
        return (await Store.GetByAttemptAsync(job.AttemptId))!;
    }

    public async Task CommitOutcomeAsync(LocalJob job,PrintOutcomeStatus status,bool retryable,string code,string message)
    {
        var draft=new AttemptOutcomeDraft(status,null,retryable,code,message,"acceptance:a29");
        var report=new ReportRequestEnvelope(
            "req-a29-"+job.AttemptId,
            AgentVersionInfo.Current,
            4,
            job.AttemptId,
            job.LocalReceiptId,
            LocalQueueStore.ToWireStatus(status),
            null,
            retryable,
            code,
            message);
        await Store.CommitOutcomeAndReportAsync(job,draft,report);
    }

    public PrintAgentService CreateService(IPrintTransport transport,IWorkerProcessFactory factory,LocalQueueStore? store=null,int workerTimeoutSeconds=20)
    {
        store??=Store;
        var dispatcher=new ReportDispatcher(store,new ReportDeliveryPolicy(jitter:()=>0.5),Log);
        var service=new PrintAgentService(Paths,store,new ReadyPrinterHealthReader(),NullLogger<PrintAgentService>.Instance,Log,new PrintWakeSignal(),dispatcher,new DurableMutationRequestStore(store),new BridgeRuntimeState(),new WorkerSupervisor(factory));
        SetField(service,"_api",transport);
        SetField(service,"_attemptStatusSupported",true);
        SetField(service,"_serverScope","server-a");
        SetField(service,"_boundServerScope","server-a");
        SetField(service,"_configurationGeneration",1L);
        SetField(service,"_nextHeartbeat",DateTimeOffset.MaxValue);
        SetField(service,"_nextDestinationRefresh",DateTimeOffset.MaxValue);
        SetField(service,"_options",new AgentOptions{WorkerTimeoutSeconds=workerTimeoutSeconds,WorkerExitProofTimeoutMilliseconds=1500,WorkerShutdownExitProofTimeoutMilliseconds=1500});
        return service;
    }

    public string ResultPath(LocalJob job)=>Path.Combine(Paths.WorkPath,$"result-{job.ServerJobId}-{job.AttemptId}.json");
    public string FencePath(LocalJob job)=>Path.Combine(Paths.WorkPath,$"fence-{job.ServerJobId}-{job.AttemptId}.dat");
    public string StartSignalPath(LocalJob job)=>Path.Combine(Paths.WorkPath,$"start-{job.ServerJobId}-{job.AttemptId}.dat");

    private static void SetField(object target,string name,object value)
    {
        var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)??throw new MissingFieldException(target.GetType().FullName,name);
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
    public IWorkerProcess Start(WorkerLaunchSpec spec){StartCount++;throw new InvalidOperationException("Worker launch is forbidden in this A29 branch.");}
}

sealed class BlockingReportTransport:IPrintTransport
{
    public TaskCompletionSource<bool> ReportStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> CancellationObserved{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)
    {
        ReportStarted.TrySetResult(true);
        return WaitForCancellationAsync(ct);
    }

    private async Task<ApiResult> WaitForCancellationAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan,ct);
            throw new UnreachableException();
        }
        catch(OperationCanceledException)
        {
            CancellationObserved.TrySetResult(true);
            throw;
        }
    }

    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>throw new NotSupportedException();
}
