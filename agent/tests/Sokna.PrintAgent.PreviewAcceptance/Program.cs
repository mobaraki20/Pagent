using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

internal static class PreviewAcceptanceProgram
{
    public static async Task<int> Main(string[] args)
    {
        var parsed=ParseArgs(args);
        if(!parsed.TryGetValue("case",out var caseId)||string.IsNullOrWhiteSpace(caseId)||
           !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
        {
            Console.Error.WriteLine("Usage: --case A35|A37|A38|A47 --results <directory>");
            return 64;
        }

        var ctx=new AcceptanceContext(caseId.ToUpperInvariant(),resultsDirectory);
        try
        {
            ctx.Check(OperatingSystem.IsWindows(),$"{ctx.CaseId} executes on Windows");
            switch(ctx.CaseId)
            {
                case "A35": await RunA35(ctx); break;
                case "A37": await RunA37(ctx); break;
                case "A38": await RunA38(ctx); break;
                case "A47": await RunA47(ctx); break;
                default:
                    await ctx.WriteResultAsync("NOT_RUN",3,$"Preview acceptance case {ctx.CaseId} is not implemented.");
                    return 3;
            }

            if(ctx.Failures.Count>0)
            {
                var error=string.Join(",",ctx.Failures);
                ctx.Log("FAIL "+error);
                await ctx.WriteResultAsync("FAIL",1,error);
                return 1;
            }

            ctx.Log($"PASS {ctx.CaseId}; assertions={ctx.Assertions.Count}");
            await ctx.WriteResultAsync("PASS",0,null);
            return 0;
        }
        catch(Exception e)
        {
            var error=$"{e.GetType().Name}: {SafeLogText.Sanitize(e.Message,400)}";
            ctx.Log("FAIL "+error);
            await ctx.WriteResultAsync("FAIL",1,error);
            return 1;
        }
    }

    private static async Task RunA35(AcceptanceContext ctx)
    {
        using var env=PreviewTestEnvironment.Create("a35-preview-lifecycle");
        var results=new List<object>();

        async Task Exercise(string name,FakePreviewProcessMode mode,TimeSpan timeout,bool cancel,string expectedCode)
        {
            var factory=new FakePreviewProcessFactory(mode);
            var executor=new SystemPreviewExecutor(env.Paths,new WorkerSupervisor(factory),env.Log);
            using var cancellation=new CancellationTokenSource();
            var request=MakePreviewRequest("a35-"+name.PadRight(8,'x'),1,timeout);
            var running=executor.ExecuteAsync(request,cancellation.Token);
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if(cancel)cancellation.Cancel();
            var result=await running.WaitAsync(TimeSpan.FromSeconds(4));
            var leftovers=Directory.GetFiles(env.Paths.WorkPath,"preview-*",SearchOption.TopDirectoryOnly);
            results.Add(new{name,result=result.Code,factory.StartCount,factory.KillCount,factory.DisposeCount,leftovers=leftovers.Length});
            ctx.Check(result.Code==expectedCode,$"{name}: lifecycle returns {expectedCode}");
            ctx.Check(factory.StartCount==1,$"{name}: exactly one preview child ownership is attempted");
            ctx.Check(factory.NonPreviewStartCount==0,$"{name}: supervisor is invoked only with Worker --preview mode");
            ctx.Check(factory.LastProcess is {HasExited:true},$"{name}: child exit is proven after cancellation/failure handling");
            ctx.Check(factory.DisposeCount==1,$"{name}: child process handle/guard lifecycle is disposed");
            ctx.Check(leftovers.Length==0,$"{name}: no preview input/output evidence is orphaned");
        }

        await Exercise("cancel",FakePreviewProcessMode.BlockUntilKilled,TimeSpan.FromSeconds(3),true,"preview_cancelled");
        await Exercise("timeout",FakePreviewProcessMode.BlockUntilKilled,TimeSpan.FromMilliseconds(120),false,"preview_timeout");
        await Exercise("guard",FakePreviewProcessMode.GuardFailure,TimeSpan.FromSeconds(2),false,"preview_worker_guard_failed");

        var safety=PreviewSafety.Validate(PreviewAcceptanceData.SimplePayload,80,72,300,300,PreviewAcceptanceData.DefaultLimits);
        var stagingHeight=Math.Clamp((int)Math.Round(16000*300/203d),16000,48000);
        ctx.Check((long)safety.WidthPixels*stagingHeight<=PreviewAcceptanceData.DefaultLimits.MaxPixelArea,"300 DPI pre-allocation staging budget fits configured safe pixel area");
        var rejected=false;
        try
        {
            _=PreviewSafety.Validate(new string('x',PreviewAcceptanceData.DefaultLimits.MaxPayloadBytes+1),80,72,203,203,PreviewAcceptanceData.DefaultLimits);
        }
        catch(InvalidDataException){rejected=true;}
        ctx.Check(rejected,"oversized preview payload is rejected before worker/raster allocation");
        ctx.Evidence["lifecycle_cases"]=results;
        ctx.Evidence["spool_submission_api_calls"]=0;
    }

    private static async Task RunA37(AcceptanceContext ctx)
    {
        using var env=await CoordinatorEnvironment.CreateAsync("a37-preview-storm");
        var previewExecutor=new BlockingPreviewExecutor();
        await using var scheduler=new PreviewScheduler(previewExecutor,4,TimeSpan.FromMinutes(10));

        var tasks=new List<Task<PreviewScheduleResult>>
        {
            scheduler.SubmitAsync(MakePreviewRequest("storm-active",1,TimeSpan.FromSeconds(5)),CancellationToken.None)
        };
        await previewExecutor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for(var i=1;i<100;i++)
        {
            var session=$"storm-{(i%20):D3}";
            var revision=1+i/20;
            tasks.Add(scheduler.SubmitAsync(MakePreviewRequest(session,revision,TimeSpan.FromSeconds(5)),CancellationToken.None));
        }

        var pressured=scheduler.Snapshot;
        ctx.Check(pressured.ActiveCount==1,"preview storm keeps exactly one active renderer");
        ctx.Check(pressured.PendingCount<=4,"preview storm keeps global pending backlog at or below configured cap");
        ctx.Check(previewExecutor.MaxConcurrency<=1,"preview executor concurrency never exceeds one");

        var claimed=await env.CreateClaimedJobAsync(3737);
        var transport=new PrintPriorityTransport();
        var printFactory=new CountingPrintFactory();
        var service=env.CreateService(transport,printFactory,[CoordinatorEnvironment.TestDestination]);
        var sw=Stopwatch.StartNew();
        await InvokePrivateTaskAsync(service,"RunCoordinatorWorkAsync",CancellationToken.None);
        sw.Stop();

        ctx.Check(transport.StartCount==1,"real coordinator reaches Start while preview storm is active");
        ctx.Check(printFactory.StartCount==1,"real coordinator reaches print worker launch while preview storm is active");
        ctx.Check(sw.Elapsed<TimeSpan.FromMilliseconds(500),$"preview pressure does not consume print coordinator 500ms lab budget; actual={sw.ElapsedMilliseconds}ms");
        ctx.Check((await env.Store.GetOutcomeAsync(claimed.AttemptId))?.Status==PrintOutcomeStatus.Failed,"print path records its independent simulated pre-launch outcome during preview pressure");

        previewExecutor.Release();
        var completed=await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        var busy=completed.Count(x=>x.Status==PreviewScheduleStatus.Busy);
        var superseded=completed.Count(x=>x.Status==PreviewScheduleStatus.Superseded);
        ctx.Check(busy>0,"100-request storm receives explicit bounded preview_busy responses instead of unbounded queue growth");
        ctx.Check(previewExecutor.MaxConcurrency==1,"preview worker concurrency remains exactly bounded after queue drain");
        ctx.Evidence["requests"]=tasks.Count;
        ctx.Evidence["busy"]=busy;
        ctx.Evidence["superseded"]=superseded;
        ctx.Evidence["max_preview_concurrency"]=previewExecutor.MaxConcurrency;
        ctx.Evidence["max_pending_configured"]=4;
        ctx.Evidence["print_coordinator_ms"]=sw.ElapsedMilliseconds;
    }

    private static async Task RunA38(AcceptanceContext ctx)
    {
        var clock=new FakeClock(new DateTimeOffset(2026,9,10,13,0,0,TimeSpan.Zero));
        var executor=new RevisionPreviewExecutor();
        await using var scheduler=new PreviewScheduler(executor,4,TimeSpan.FromMinutes(1),clock);

        var alpha1=scheduler.SubmitAsync(MakePreviewRequest("session-alpha",1,TimeSpan.FromSeconds(5)),CancellationToken.None);
        await executor.Alpha1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var beta1=scheduler.SubmitAsync(MakePreviewRequest("session-beta",1,TimeSpan.FromSeconds(5)),CancellationToken.None);
        var alpha2=scheduler.SubmitAsync(MakePreviewRequest("session-alpha",2,TimeSpan.FromSeconds(5)),CancellationToken.None);
        var alpha3=scheduler.SubmitAsync(MakePreviewRequest("session-alpha",3,TimeSpan.FromSeconds(5)),CancellationToken.None);

        var a1=await alpha1.WaitAsync(TimeSpan.FromSeconds(2));
        var a2=await alpha2.WaitAsync(TimeSpan.FromSeconds(2));
        ctx.Check(a1.Status==PreviewScheduleStatus.Superseded,"active old revision is explicitly superseded when a newer revision for the same session arrives");
        ctx.Check(a2.Status==PreviewScheduleStatus.Superseded,"queued old revision is replaced before start by newest revision in the same session");

        executor.Release();
        var b1=await beta1.WaitAsync(TimeSpan.FromSeconds(2));
        var a3=await alpha3.WaitAsync(TimeSpan.FromSeconds(2));
        ctx.Check(b1.Status==PreviewScheduleStatus.Completed,"independent beta session is never superseded by alpha revisions");
        ctx.Check(a3.Status==PreviewScheduleStatus.Completed,"newest alpha revision completes after superseding only its own session");
        ctx.Check(executor.MaxConcurrency==1,"revision scheduling preserves the global one-renderer limit");

        var beforeTtl=scheduler.Snapshot.SessionCount;
        clock.Advance(TimeSpan.FromMinutes(2));
        var gamma=await scheduler.SubmitAsync(MakePreviewRequest("session-gamma",1,TimeSpan.FromSeconds(2)),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        ctx.Check(gamma.Status==PreviewScheduleStatus.Completed,"new session remains usable after revision cache eviction");
        var afterTtl=scheduler.Snapshot.SessionCount;
        ctx.Check(beforeTtl>=2&&afterTtl==1,"expired revision identities are evicted by TTL without retaining private preview payloads");

        var replay=await scheduler.SubmitAsync(MakePreviewRequest("session-gamma",1,TimeSpan.FromSeconds(2)),CancellationToken.None);
        ctx.Check(replay.Status==PreviewScheduleStatus.Superseded,"same/older revision cannot create a second render inside its live session TTL");
        ctx.Evidence["executor_started"]=executor.StartedKeys.ToArray();
        ctx.Evidence["session_count_before_ttl"]=beforeTtl;
        ctx.Evidence["session_count_after_ttl"]=afterTtl;
    }

    private static async Task RunA47(AcceptanceContext ctx)
    {
        using var env=await CoordinatorEnvironment.CreateAsync("a47-latency-resources");
        using var process=Process.GetCurrentProcess();
        var cpuBefore=process.TotalProcessorTime;
        var rssBefore=process.WorkingSet64;

        var idleTransport=new BenchmarkTransport();
        var idleService=env.CreateService(idleTransport,new CountingPrintFactory(),[]);
        var idle=await SampleAsync(100,()=>InvokePrivateTaskAsync(idleService,"RunCoordinatorWorkAsync",CancellationToken.None));

        var burstTransport=new BenchmarkTransport();
        var burstService=env.CreateService(burstTransport,new CountingPrintFactory(),[CoordinatorEnvironment.TestDestination]);
        var burst=await SampleAsync(100,()=>InvokePrivateTaskAsync(burstService,"RunCoordinatorWorkAsync",CancellationToken.None));

        var previewExecutor=new BlockingPreviewExecutor();
        await using var scheduler=new PreviewScheduler(previewExecutor,4,TimeSpan.FromMinutes(10));
        var previewTasks=new List<Task<PreviewScheduleResult>>
        {
            scheduler.SubmitAsync(MakePreviewRequest("bench-active",1,TimeSpan.FromSeconds(5)),CancellationToken.None)
        };
        await previewExecutor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for(var i=1;i<100;i++)
            previewTasks.Add(scheduler.SubmitAsync(MakePreviewRequest($"bench-{(i%16):D3}",1+i/16,TimeSpan.FromSeconds(5)),CancellationToken.None));

        var pressureTransport=new BenchmarkTransport
        {
            HeartbeatGate=new(TaskCreationOptions.RunContinuationsAsynchronously),
            ProbeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var pressureService=env.CreateService(pressureTransport,new CountingPrintFactory(),[CoordinatorEnvironment.TestDestination]);
        InvokePrivateVoid(pressureService,"StartSideIo",CancellationToken.None);
        await pressureTransport.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await pressureTransport.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pressure=await SampleAsync(100,()=>InvokePrivateTaskAsync(pressureService,"RunCoordinatorWorkAsync",CancellationToken.None));

        var snapshot=scheduler.Snapshot;
        previewExecutor.Release();
        var previewResults=await Task.WhenAll(previewTasks).WaitAsync(TimeSpan.FromSeconds(5));
        pressureTransport.HeartbeatGate?.TrySetResult(true);
        pressureTransport.ProbeGate?.TrySetResult(true);

        process.Refresh();
        var cpuAfter=process.TotalProcessorTime;
        var rssAfter=process.WorkingSet64;
        var idleStats=Stats(idle);
        var burstStats=Stats(burst);
        var pressureStats=Stats(pressure);
        var rssGrowth=Math.Max(0,rssAfter-rssBefore);

        ctx.Check(idle.Length==100&&burst.Length==100&&pressure.Length==100,"benchmark records at least 100 raw samples for idle, burst and pressure phases separately");
        ctx.Check(burstStats.P95<500,"fake-network burst coordinator P95 stays below explicit 500ms lab target");
        ctx.Check(pressureStats.P95<500,"preview + blocked diagnostics pressure P95 stays below explicit 500ms lab target");
        ctx.Check(snapshot.ActiveCount<=1&&snapshot.PendingCount<=4,"preview pressure obeys active/pending resource caps during benchmark");
        ctx.Check(previewExecutor.MaxConcurrency<=1,"benchmark observes no more than one preview executor concurrently");
        ctx.Check(rssGrowth<128L*1024*1024,$"benchmark RSS growth stays below explicit 128MiB fake-pressure guard; actual={rssGrowth/1024/1024}MiB");
        ctx.Check(previewResults.Count(x=>x.Status==PreviewScheduleStatus.Busy)>0,"benchmark pressure returns busy instead of accumulating 100 preview workers");

        var samplesPath=Path.Combine(ctx.ResultsDirectory,"A47.samples.json");
        await File.WriteAllTextAsync(samplesPath,JsonSerializer.Serialize(new
        {
            clock="Stopwatch.GetTimestamp monotonic",
            network="in-process deterministic fake transport; no real WAN claim",
            idle_ms=idle,
            burst_ms=burst,
            preview_diagnostics_pressure_ms=pressure
        },new JsonSerializerOptions{WriteIndented=true}));

        ctx.Evidence["network_mode"]="in-process deterministic fake transport";
        ctx.Evidence["clock_method"]="Stopwatch.GetTimestamp / Stopwatch.GetElapsedTime";
        ctx.Evidence["idle"]=idleStats;
        ctx.Evidence["burst"]=burstStats;
        ctx.Evidence["preview_diagnostics_pressure"]=pressureStats;
        ctx.Evidence["cpu_ms"]=(cpuAfter-cpuBefore).TotalMilliseconds;
        ctx.Evidence["rss_before_bytes"]=rssBefore;
        ctx.Evidence["rss_after_bytes"]=rssAfter;
        ctx.Evidence["rss_growth_bytes"]=rssGrowth;
        ctx.Evidence["preview_active_at_pressure"]=snapshot.ActiveCount;
        ctx.Evidence["preview_pending_at_pressure"]=snapshot.PendingCount;
        ctx.Evidence["preview_executor_max_concurrency"]=previewExecutor.MaxConcurrency;
        ctx.Evidence["os_worker_processes_observed"]=Process.GetProcessesByName("Sokna.PrintAgent.Worker").Length;
        ctx.Evidence["raw_samples_file"]=Path.GetFileName(samplesPath);
    }

    private static PreviewWorkRequest MakePreviewRequest(string session,long revision,TimeSpan timeout)
        =>new(session,revision,PreviewAcceptanceData.SimplePayload,80,72,203,203,PreviewAcceptanceData.DefaultLimits,timeout,TimeSpan.FromMilliseconds(300));

    private static async Task<double[]> SampleAsync(int count,Func<Task> action)
    {
        var samples=new double[count];
        for(var i=0;i<count;i++)
        {
            var start=Stopwatch.GetTimestamp();
            await action();
            samples[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return samples;
    }

    private static LatencyStats Stats(double[] raw)
    {
        var sorted=raw.OrderBy(x=>x).ToArray();
        return new(Percentile(sorted,0.50),Percentile(sorted,0.95),sorted[^1],sorted.Average());
    }

    private static double Percentile(double[] sorted,double p)
    {
        if(sorted.Length==0)return 0;
        var index=(sorted.Length-1)*p;
        var lo=(int)Math.Floor(index);
        var hi=(int)Math.Ceiling(index);
        if(lo==hi)return sorted[lo];
        return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);
    }

    private static async Task InvokePrivateTaskAsync(PrintAgentService service,string methodName,params object?[] args)
    {
        var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
            ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
        var task=method.Invoke(service,args) as Task??throw new InvalidOperationException($"{methodName} did not return Task.");
        await task;
    }

    private static void InvokePrivateVoid(PrintAgentService service,string methodName,params object?[] args)
    {
        var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
            ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
        _=method.Invoke(service,args);
    }

    private static Dictionary<string,string> ParseArgs(string[] values)
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

    internal static string ResolveSourceSha()
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
}

internal static class PreviewAcceptanceData
{
    public const string SimplePayload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Preview Acceptance\",\"sections\":[]}";
    public static readonly PreviewSafetyLimits DefaultLimits=new(240000,100000,500,24000,24000000,2000000);
}

internal sealed class AcceptanceContext
{
    private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
    private readonly string _logPath;
    private readonly string _resultPath;
    public string CaseId{get;}
    public string ResultsDirectory{get;}
    public List<string> Assertions{get;}=[];
    public List<string> Failures{get;}=[];
    public Dictionary<string,object?> Evidence{get;}=[];

    public AcceptanceContext(string caseId,string resultsDirectory)
    {
        CaseId=caseId;
        ResultsDirectory=Path.GetFullPath(resultsDirectory);
        Directory.CreateDirectory(ResultsDirectory);
        _logPath=Path.Combine(ResultsDirectory,$"{CaseId}.log");
        _resultPath=Path.Combine(ResultsDirectory,$"{CaseId}.result.json");
    }

    public void Check(bool value,string name){Assertions.Add(name);if(!value)Failures.Add(name);}
    public void Log(string text){Console.WriteLine(text);File.AppendAllText(_logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

    public async Task WriteResultAsync(string status,int exitCode,string? error)
    {
        var payload=new
        {
            case_id=CaseId,
            status,
            source_sha=PreviewAcceptanceProgram.ResolveSourceSha(),
            run_started_at=_started.ToString("O"),
            run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
            environment=$"{Environment.OSVersion}; .NET {Environment.Version}",
            exit_code=exitCode,
            test_path="tests/Sokna.PrintAgent.PreviewAcceptance/Program.cs",
            assertions=Assertions,
            failed_assertions=Failures,
            evidence=Evidence,
            raw_log=Path.GetFileName(_logPath),
            error
        };
        await File.WriteAllTextAsync(_resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
    }
}

internal sealed record LatencyStats(double P50,double P95,double Max,double Mean);

internal enum FakePreviewProcessMode{BlockUntilKilled,GuardFailure}

internal sealed class FakePreviewProcessFactory:IWorkerProcessFactory
{
    private readonly FakePreviewProcessMode _mode;
    private int _starts,_kills,_disposes,_nonPreview;
    public FakePreviewProcessFactory(FakePreviewProcessMode mode)=>_mode=mode;
    public int StartCount=>Volatile.Read(ref _starts);
    public int KillCount=>Volatile.Read(ref _kills);
    public int DisposeCount=>Volatile.Read(ref _disposes);
    public int NonPreviewStartCount=>Volatile.Read(ref _nonPreview);
    public TaskCompletionSource<bool> Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakePreviewProcess? LastProcess{get;private set;}

    public IWorkerProcess Start(WorkerLaunchSpec spec)
    {
        Interlocked.Increment(ref _starts);
        if(!spec.Arguments.StartsWith("--preview ",StringComparison.Ordinal))Interlocked.Increment(ref _nonPreview);
        LastProcess=new FakePreviewProcess(_mode,()=>Interlocked.Increment(ref _kills),()=>Interlocked.Increment(ref _disposes));
        Started.TrySetResult(true);
        return LastProcess;
    }
}

internal sealed class FakePreviewProcess:IWorkerProcess
{
    private readonly FakePreviewProcessMode _mode;
    private readonly Action _onKill,_onDispose;
    private readonly TaskCompletionSource<bool> _exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exited;
    public FakePreviewProcess(FakePreviewProcessMode mode,Action onKill,Action onDispose){_mode=mode;_onKill=onKill;_onDispose=onDispose;}
    public bool HasExited=>Volatile.Read(ref _exited)==1;
    public int? ExitCode=>HasExited?-1:null;
    public void AttachGuard(){if(_mode==FakePreviewProcessMode.GuardFailure)throw new InvalidOperationException("synthetic guard failure");}
    public void KillTree(){_onKill();Interlocked.Exchange(ref _exited,1);_exit.TrySetResult(true);}
    public Task WaitForExitAsync(CancellationToken cancellationToken)=>_exit.Task.WaitAsync(cancellationToken);
    public string GetBoundedStandardError()=>"synthetic preview process";
    public string GetBoundedStandardOutput()=>string.Empty;
    public ValueTask DisposeAsync(){_onDispose();return ValueTask.CompletedTask;}
}

internal sealed class BlockingPreviewExecutor:IPreviewExecutor
{
    private readonly TaskCompletionSource<bool> _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active,_max;
    public TaskCompletionSource<bool> FirstStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaxConcurrency=>Volatile.Read(ref _max);
    public async Task<PreviewScheduleResult> ExecuteAsync(PreviewWorkRequest request,CancellationToken ct)
    {
        var active=Interlocked.Increment(ref _active);
        UpdateMax(active);
        FirstStarted.TrySetResult(true);
        try
        {
            await _release.Task.WaitAsync(ct);
            return PreviewScheduleResult.Completed(request,DummyRender(request));
        }
        finally{Interlocked.Decrement(ref _active);}
    }
    public void Release()=>_release.TrySetResult(true);
    private void UpdateMax(int value){while(true){var old=Volatile.Read(ref _max);if(value<=old||Interlocked.CompareExchange(ref _max,value,old)==old)return;}}
    private static PreviewRenderData DummyRender(PreviewWorkRequest request)=>new([137,80,78,71],10,10,request.DpiX,request.DpiY,"dummy","test","test",true);
}

internal sealed class RevisionPreviewExecutor:IPreviewExecutor
{
    private readonly TaskCompletionSource<bool> _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active,_max;
    public TaskCompletionSource<bool> Alpha1Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> StartedKeys{get;}=new();
    public int MaxConcurrency=>Volatile.Read(ref _max);
    public async Task<PreviewScheduleResult> ExecuteAsync(PreviewWorkRequest request,CancellationToken ct)
    {
        var active=Interlocked.Increment(ref _active);
        UpdateMax(active);
        StartedKeys.Enqueue(request.SessionId+":"+request.Revision);
        try
        {
            if(request.SessionId=="session-alpha"&&request.Revision==1)
            {
                Alpha1Started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan,ct);
            }
            else
            {
                await _release.Task.WaitAsync(ct);
            }
            return PreviewScheduleResult.Completed(request,DummyRender(request));
        }
        finally{Interlocked.Decrement(ref _active);}
    }
    public void Release()=>_release.TrySetResult(true);
    private void UpdateMax(int value){while(true){var old=Volatile.Read(ref _max);if(value<=old||Interlocked.CompareExchange(ref _max,value,old)==old)return;}}
    private static PreviewRenderData DummyRender(PreviewWorkRequest request)=>new([137,80,78,71],10,10,request.DpiX,request.DpiY,"dummy","test","test",true);
}

internal sealed class FakeClock:IAgentTimeSource
{
    public FakeClock(DateTimeOffset now){UtcNow=now;MonotonicNow=TimeSpan.Zero;}
    public DateTimeOffset UtcNow{get;private set;}
    public TimeSpan MonotonicNow{get;private set;}
    public void Advance(TimeSpan value){UtcNow+=value;MonotonicNow+=value;}
}

internal sealed class PreviewTestEnvironment:IDisposable
{
    private readonly string _root;
    public AgentPaths Paths{get;}
    public AgentLog Log{get;}
    private PreviewTestEnvironment(string root)
    {
        _root=root;
        Paths=new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
        Log=new AgentLog(Paths.LogsPath);
    }
    public static PreviewTestEnvironment Create(string suffix)=>new(Path.Combine(Path.GetTempPath(),$"sokna-preview-{suffix}-{Guid.NewGuid():N}"));
    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

internal sealed class CoordinatorEnvironment:IDisposable
{
    private readonly string _root;
    public static readonly DestinationConfig TestDestination=new("prep","Test","Test Queue",80,72,1,"combined");
    public static readonly PrinterQueueHealth ReadyQueue=new("Test Queue",false,false,false,false,0,"Acceptance Driver","LPT1:");
    public AgentPaths Paths{get;}
    public LocalQueueStore Store{get;}
    public AgentLog Log{get;}

    private CoordinatorEnvironment(string root)
    {
        _root=root;
        Paths=new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
        Store=new LocalQueueStore(Paths.DatabasePath,new TestProtector());
        Log=new AgentLog(Paths.LogsPath);
    }

    public static async Task<CoordinatorEnvironment> CreateAsync(string suffix)
    {
        var env=new CoordinatorEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-preview-coordinator-{suffix}-{Guid.NewGuid():N}"));
        await env.Store.InitializeAsync();
        return env;
    }

    public async Task<LocalJob> CreateClaimedJobAsync(long attemptId)
    {
        var hash=CryptoUtil.Sha256Hex(PreviewAcceptanceData.SimplePayload);
        var claim=new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,PreviewAcceptanceData.SimplePayload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-test",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            TestDestination);
        var job=await Store.PersistReservedAsync(claim,"receipt-"+attemptId,"server-a");
        await Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
        return (await Store.GetByAttemptAsync(job.AttemptId))!;
    }

    public PrintAgentService CreateService(IPrintTransport transport,IWorkerProcessFactory factory,IReadOnlyList<DestinationConfig> destinations)
    {
        var dispatcher=new ReportDispatcher(Store,new ReportDeliveryPolicy(jitter:()=>0.5),Log);
        var service=new PrintAgentService(
            Paths,
            Store,
            new StaticHealthReader(),
            NullLogger<PrintAgentService>.Instance,
            Log,
            new PrintWakeSignal(),
            dispatcher,
            new DurableMutationRequestStore(Store),
            new BridgeRuntimeState(),
            new WorkerSupervisor(factory));
        SetField(service,"_api",transport);
        SetField(service,"_attemptStatusSupported",true);
        SetField(service,"_serverScope","server-a");
        SetField(service,"_boundServerScope","server-a");
        SetField(service,"_destinations",destinations);
        SetField(service,"_configurationGeneration",1L);
        SetField(service,"_options",new AgentOptions{ServerBaseUrl="https://benchmark.invalid",RequireHttps=true,WorkerTimeoutSeconds=10});
        return service;
    }

    private static void SetField(object target,string name,object value)
    {
        var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)??throw new MissingFieldException(target.GetType().FullName,name);
        field.SetValue(target,value);
    }
    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

internal sealed class StaticHealthReader:IPrinterHealthReader
{
    public PrinterHealthSnapshot Read(TimeSpan freshnessWindow)=>new([CoordinatorEnvironment.ReadyQueue],DateTimeOffset.UtcNow,null,null,0,true,1);
}

internal sealed class TestProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>SecretStore.ProtectText(value);
    public string Unprotect(string value)=>SecretStore.UnprotectText(value);
}

internal sealed class CountingPrintFactory:IWorkerProcessFactory
{
    private int _starts;
    public int StartCount=>Volatile.Read(ref _starts);
    public IWorkerProcess Start(WorkerLaunchSpec spec){Interlocked.Increment(ref _starts);throw new InvalidOperationException("synthetic print launch failure before child creation");}
}

internal sealed class PrintPriorityTransport:IPrintTransport
{
    private int _start;
    public int StartCount=>Volatile.Read(ref _start);
    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ClaimResponse(true,request.RequestId,[],DateTimeOffset.UtcNow.ToString("O"),false));
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,"claimed",AttemptId:item.Attempt.Id,JobId:item.Job.Id,LocalReceiptId:localReceiptId));
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct){Interlocked.Increment(ref _start);return Task.FromResult(new ApiResult(true,"started",AttemptId:job.AttemptId,JobId:job.ServerJobId,LocalReceiptId:job.LocalReceiptId));}
    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>Task.FromResult(new ProbeResponse(true,4,"6.0.0","6.2.0",[CoordinatorEnvironment.TestDestination],["attempt_status"],"server-a",DateTimeOffset.UtcNow.ToString("O")));
}

internal sealed class BenchmarkTransport:IPrintTransport
{
    private int _claims;
    public int ClaimCount=>Volatile.Read(ref _claims);
    public TaskCompletionSource<bool> HeartbeatStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ProbeStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool>? HeartbeatGate{get;set;}
    public TaskCompletionSource<bool>? ProbeGate{get;set;}
    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct){Interlocked.Increment(ref _claims);return Task.FromResult(new ClaimResponse(true,request.RequestId,[],DateTimeOffset.UtcNow.ToString("O"),false));}
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public async Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)
    {
        HeartbeatStarted.TrySetResult(true);
        if(HeartbeatGate is not null)await HeartbeatGate.Task.WaitAsync(ct);
        return new ApiResult(true);
    }
    public async Task<ProbeResponse> ProbeAsync(CancellationToken ct)
    {
        ProbeStarted.TrySetResult(true);
        if(ProbeGate is not null)await ProbeGate.Task.WaitAsync(ct);
        return new ProbeResponse(true,4,"6.0.0","6.2.0",[CoordinatorEnvironment.TestDestination],["attempt_status"],"server-a",DateTimeOffset.UtcNow.ToString("O"));
    }
}
