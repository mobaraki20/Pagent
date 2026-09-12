using System.Collections.Concurrent;
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
    Console.Error.WriteLine("Usage: --case A12|A13|A14|A15|A16|A19|A20|A21|A22|A23|A30 --results <directory>");
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
        case "A12": await RunA12(); break;
        case "A13": await RunA13(); break;
        case "A14": await RunA14(); break;
        case "A15": await RunA15(); break;
        case "A16": await RunA16(); break;
        case "A19": await RunA19(); break;
        case "A20": await RunA20(); break;
        case "A21": await RunA21(); break;
        case "A22": await RunA22(); break;
        case "A23": await RunA23(); break;
        case "A30": await RunA30(); break;
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

async Task RunA12()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a12-accept-lost-response");
    var leaseExpiry=DateTimeOffset.UtcNow.AddSeconds(5);
    var job=await env.CreateJobAsync(2612,"receipt-a12",leaseExpiry);
    await using var server=new LoopbackPrintApiServer([
        LoopbackResponseKind.DisconnectAfterCommit,
        LoopbackResponseKind.AttemptStatusClaimed]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    var processFactory=new CountingNoStartFactory();
    var firstService=env.CreateService(transport,true,processFactory);

    var firstFailed=await TryInvokePrivateAsync(firstService,"AcceptAnyReservedAsync");
    var afterLostAck=await env.Store.GetByAttemptAsync(job.AttemptId);
    var durableAccept=await env.Store.GetMetaAsync($"accept_request_v2:{job.AttemptId}");
    Check(firstFailed,"lost accept response surfaces as unresolved API operation");
    Check(afterLostAck?.State==LocalJobState.Reserved,"lost accept response keeps reservation unresolved");
    Check(server.Requests.Count==1&&IsAccept(server.Requests[0]),"first pass traverses real accept HTTP request");
    Check(!string.IsNullOrWhiteSpace(durableAccept),"accept request identity/body remains durable after lost ACK");
    using(var wire=JsonDocument.Parse(server.Requests[0].Body))
    using(var stored=JsonDocument.Parse(durableAccept!))
    {
        Check(wire.RootElement.GetProperty("request_id").GetString()==stored.RootElement.GetProperty("request_id").GetString(),"wire accept request_id equals durable request_id");
        Check(wire.RootElement.GetProperty("attempt_id").GetInt64()==stored.RootElement.GetProperty("attempt_id").GetInt64(),"wire accept attempt identity equals durable identity");
        Check(wire.RootElement.GetProperty("local_receipt_id").GetString()==stored.RootElement.GetProperty("local_receipt_id").GetString(),"wire accept receipt equals durable receipt");
    }

    var remaining=leaseExpiry-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(150);
    if(remaining>TimeSpan.Zero)await Task.Delay(remaining);
    var restartedStore=await env.CreateRestartedStoreAsync();
    var restartedService=env.CreateService(transport,true,processFactory,restartedStore);
    await InvokePrivateAsync(restartedService,"AcceptAnyReservedAsync");
    var reconciled=await restartedStore.GetByAttemptAsync(job.AttemptId);

    Check(reconciled?.State==LocalJobState.Claimed,"same attempt continues only after authoritative claimed confirmation");
    Check(server.Requests.Count==2&&IsAttemptStatus(server.Requests[1]),"restart reconciles through attempt_status instead of issuing a second accept");
    Check(server.Requests.Count(r=>IsAccept(r))==1,"lost accept ACK never creates a second accept mutation");
    Check(processFactory.StartCount==0,"accept reconciliation itself never invokes worker submission");
    Check(await restartedStore.GetMetaAsync($"accept_request_v2:{job.AttemptId}") is null,"durable accept request clears only after authoritative continuation is confirmed");
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

        await using var diagnosticServer=new LoopbackPrintApiServer([scenario.Response]);
        using var diagnosticHttp=new HttpClient();
        var diagnosticTransport=new HttpPrintTransport(diagnosticHttp,diagnosticServer.BaseUrl,"acceptance-token",env.Protector);
        string? observedCode=null;
        try{_ = await diagnosticTransport.AttemptStatusAsync(job,CancellationToken.None);}
        catch(PrintProtocolException e){observedCode=e.Code;}
        Check(observedCode==scenario.ExpectedCode,$"{scenario.Name}: typed protocol code is {scenario.ExpectedCode}");
    }
}

async Task RunA15()
{
    using(var env=await ServiceTestEnvironment.CreateAsync("a15-lost-start-ack"))
    {
        var job=await env.CreateJobAsync(2615,"receipt-a15-lost",DateTimeOffset.UtcNow.AddMinutes(5));
        await env.Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
        await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.DisconnectAfterCommit,LoopbackResponseKind.Success]);
        using var http=new HttpClient();
        var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
        var processFactory=new CountingNoStartFactory();
        var firstService=env.CreateService(transport,true,processFactory);

        var lost=await TryInvokePrivateAsync(firstService,"ProcessOneAsync");
        var afterLost=await env.Store.GetByAttemptAsync(job.AttemptId);
        var durableStart=await env.Store.GetMetaAsync($"start_request_v2:{job.AttemptId}");
        Check(lost,"lost start response leaves mutation unresolved");
        Check(afterLost?.State==LocalJobState.Claimed,"lost start ACK does not advance to WorkerLaunching");
        Check(!string.IsNullOrWhiteSpace(durableStart),"start request survives lost ACK durably");
        Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"lost-ACK pass traverses real start HTTP request");
        Check(processFactory.StartCount==0,"worker cannot launch before valid start confirmation");

        var restartedStore=await env.CreateRestartedStoreAsync();
        var restartedService=env.CreateService(transport,true,processFactory,restartedStore);
        await InvokePrivateAsync(restartedService,"ProcessOneAsync");
        var outcome=await restartedStore.GetOutcomeAsync(job.AttemptId);
        Check(server.Requests.Count==2&&server.Requests.All(IsStart),"restart replays only the start mutation before local worker launch");
        Check(server.Requests[0].Body==server.Requests[1].Body,"lost start ACK replay preserves request body byte-for-byte");
        Check(processFactory.StartCount==1,"valid replay confirmation permits exactly one worker launch attempt");
        Check(outcome is {Status:PrintOutcomeStatus.Failed,Retryable:true},"simulated worker launch failure remains a proven pre-fence failure");
    }

    using(var env=await ServiceTestEnvironment.CreateAsync("a15-post-ack-crash-window"))
    {
        var job=await env.CreateJobAsync(2715,"receipt-a15-crash",DateTimeOffset.UtcNow.AddMinutes(5));
        await env.Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
        var collision=env.InputPath(job);
        Directory.CreateDirectory(collision);
        await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.Success,LoopbackResponseKind.Success]);
        using var http=new HttpClient();
        var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
        var processFactory=new CountingNoStartFactory();
        var firstService=env.CreateService(transport,true,processFactory);

        var writeFault=await TryInvokePrivateAsync(firstService,"ProcessOneAsync");
        var afterFault=await env.Store.GetByAttemptAsync(job.AttemptId);
        Check(writeFault,"fault after start ACK and before durable WorkerLaunching record is observable");
        Check(afterFault?.State==LocalJobState.Claimed,"post-ACK local persistence fault leaves attempt at last durable state");
        Check(processFactory.StartCount==0,"post-ACK persistence fault launches no worker");
        Check(await env.Store.GetMetaAsync($"start_request_v2:{job.AttemptId}") is not null,"start request is retained across post-ACK crash window");
        Check(server.Requests.Count==1&&IsStart(server.Requests[0]),"server received first start before injected local persistence fault");

        Directory.Delete(collision,true);
        var restartedStore=await env.CreateRestartedStoreAsync();
        var restartedService=env.CreateService(transport,true,processFactory,restartedStore);
        await InvokePrivateAsync(restartedService,"ProcessOneAsync");
        Check(server.Requests.Count==2&&server.Requests.All(IsStart),"restart replays start with same durable idempotency identity");
        Check(server.Requests[0].Body==server.Requests[1].Body,"post-ACK crash replay preserves start body byte-for-byte");
        Check(processFactory.StartCount==1,"post-ACK crash recovery still permits at most one worker launch attempt");
    }
}

async Task RunA16()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a16-legacy-server");
    var job=await env.CreateJobAsync(2616,"receipt-a16",DateTimeOffset.UtcNow.AddMinutes(-1));
    await using var server=new LoopbackPrintApiServer([LoopbackResponseKind.ProbeLegacyNoAttemptStatus]);
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",env.Protector);
    var probe=await transport.ProbeAsync(CancellationToken.None);
    var supports=ServerScopeResolver.Supports(probe,"attempt_status");
    Check(probe.Success&&probe.ProtocolVersion==4,"legacy fixture is a valid v4 probe response");
    Check(!supports,"legacy fixture explicitly lacks attempt_status capability");

    var processFactory=new CountingNoStartFactory();
    var service=env.CreateService(transport,false,processFactory);
    await InvokePrivateAsync(service,"AcceptAnyReservedAsync");
    await InvokePrivateAsync(service,"AcceptAnyReservedAsync");
    var after=await env.Store.GetByAttemptAsync(job.AttemptId);
    Check(after?.State==LocalJobState.RecoveryHold,"expired reservation on server without attempt_status enters explicit safe incompatibility hold");
    Check(after?.LastError?.Contains("attempt_status",StringComparison.OrdinalIgnoreCase)==true,"operator-visible hold reason names missing attempt_status capability");
    Check(processFactory.StartCount==0,"legacy capability fallback never launches worker by guess");
    Check(server.Requests.Count==1&&IsProbe(server.Requests[0]),"legacy server receives probe only; no repeated 404 attempt_status loop");
    Check(server.Requests.All(r=>!IsAttemptStatus(r)&&!IsStart(r)),"missing capability causes neither attempt_status request nor guessed start");
}

async Task RunA19()
{
    // Warm the coordinator/JIT path before measuring. GitHub-hosted runners can spend
    // more than the product budget compiling this path on its first invocation; that
    // startup noise is not the idle-poll delay A19 is intended to detect.
    using(var warmup=await ServiceTestEnvironment.CreateAsync("a19-warmup"))
    {
        var warmupEvents=new ConcurrentQueue<string>();
        var warmupTransport=new CoordinatorTransport(warmup.CreateClaimItem(2919),warmupEvents);
        var warmupService=warmup.CreateService(warmupTransport,true,new CountingNoStartFactory(warmupEvents),destinations:[ServiceTestEnvironment.TestDestination]);
        await InvokePrivateAsync(warmupService,"RunCoordinatorWorkAsync");
    }

    using var env=await ServiceTestEnvironment.CreateAsync("a19-no-post-claim-delay");
    var events=new ConcurrentQueue<string>();
    var claim=env.CreateClaimItem(3019);
    var transport=new CoordinatorTransport(claim,events);
    var processFactory=new CountingNoStartFactory(events);
    var service=env.CreateService(transport,true,processFactory,destinations:[ServiceTestEnvironment.TestDestination]);

    var sw=Stopwatch.StartNew();
    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    sw.Stop();
    var sequence=events.ToArray();

    Check(transport.ClaimCount==1,"coordinator performs one claim");
    Check(transport.AcceptCount==1,"successful claim is accepted in the same coordinator iteration");
    Check(transport.StartCount==1,"accepted work reaches start in the same coordinator iteration");
    Check(processFactory.StartCount==1,"same iteration reaches exactly one worker launch attempt");
    Check(IsOrdered(sequence,"claim","accept","start","worker"),"event order is claim -> accept -> start -> worker without an idle poll between stages");
    Check(sw.Elapsed<TimeSpan.FromMilliseconds(500),$"lab coordinator path stays below 500ms without injected I/O delay; actual={sw.ElapsedMilliseconds}ms");
}

async Task RunA20()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a20-wake-coalescing-owner");
    var job=await env.CreateJobAsync(3020,"receipt-a20",DateTimeOffset.UtcNow.AddMinutes(5));
    await env.Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);
    var events=new ConcurrentQueue<string>();
    var transport=new CoordinatorTransport(null,events){StartGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
    var wake=new PrintWakeSignal();
    var processFactory=new CountingNoStartFactory(events);
    var service=env.CreateService(transport,true,processFactory,wake:wake);

    var first=InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    await transport.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    for(var i=0;i<100;i++)wake.Pulse();

    var second=InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    var secondCompleted=await Task.WhenAny(second,Task.Delay(300))==second;
    Check(secondCompleted,"concurrent poll attempt is rejected by the single coordinator owner instead of waiting behind it");

    transport.StartGate.TrySetResult(true);
    await first;
    Check(transport.StartCount==1,"concurrent wake/poll pressure creates one start mutation for the attempt");
    Check(processFactory.StartCount==1,"concurrent wake/poll pressure creates one worker launch attempt");

    var firstWake=Stopwatch.StartNew();
    await wake.WaitOrDelayAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
    firstWake.Stop();
    Check(firstWake.Elapsed<TimeSpan.FromMilliseconds(150),"at least one wake raised while busy is retained");

    var drained=Stopwatch.StartNew();
    await wake.WaitOrDelayAsync(TimeSpan.FromMilliseconds(120),CancellationToken.None);
    drained.Stop();
    Check(drained.Elapsed>=TimeSpan.FromMilliseconds(80),"repeated wake storm is coalesced instead of creating an unbounded wake backlog");
}

async Task RunA21()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a21-slow-diagnostics");
    var job=await env.CreateJobAsync(3021,"receipt-a21",DateTimeOffset.UtcNow.AddMinutes(5));
    await env.Store.SetStateAsync(job.AttemptId,LocalJobState.Claimed);

    var clock=new SystemAgentTimeSource();
    var printerState=new PrinterHealthState(clock);
    printerState.MarkSuccess([ServiceTestEnvironment.ReadyQueue]);
    var wake=new PrintWakeSignal();
    using var blockingProvider=new BlockingPrinterProvider();
    var discovery=new PrinterDiscoveryService(blockingProvider,printerState,wake,NullLogger<PrinterDiscoveryService>.Instance);
    await discovery.StartAsync(CancellationToken.None);
    await blockingProvider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var transport=new CoordinatorTransport(null,new ConcurrentQueue<string>())
    {
        HeartbeatGate=new(TaskCreationOptions.RunContinuationsAsynchronously),
        ProbeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)
    };
    var processFactory=new CountingNoStartFactory();
    var service=env.CreateService(transport,true,processFactory,health:printerState,wake:wake);

    InvokePrivateVoid(service,"StartSideIo",CancellationToken.None);
    await transport.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await transport.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // This case proves isolation from diagnostics budgets, not a sub-500ms product SLA. The
    // shortest real side-I/O timeout is 3 seconds, so completion within 2 seconds while both
    // gates are still blocked proves that ready print work did not inherit those timeouts.
    var sw=Stopwatch.StartNew();
    var coordinator=InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    var completedBeforeDiagnosticsTimeout=await Task.WhenAny(coordinator,Task.Delay(TimeSpan.FromSeconds(2)))==coordinator;
    sw.Stop();
    var heartbeatStillBlocked=!transport.HeartbeatGate.Task.IsCompleted;
    var probeStillBlocked=!transport.ProbeGate.Task.IsCompleted;

    Check(completedBeforeDiagnosticsTimeout,$"ready work completes before the shortest 3s diagnostics timeout; observed={sw.ElapsedMilliseconds}ms");
    Check(heartbeatStillBlocked,"heartbeat is still deliberately blocked when print coordinator completes");
    Check(probeStillBlocked,"destination refresh is still deliberately blocked when print coordinator completes");
    Check(processFactory.StartCount==1,"ready work reaches worker while heartbeat/probe are blocked");
    Check(blockingProvider.CallCount==1,"blocked native discovery has one in-flight owner and is not fanned out");

    transport.HeartbeatGate.TrySetResult(true);
    transport.ProbeGate.TrySetResult(true);
    blockingProvider.Release();
    if(completedBeforeDiagnosticsTimeout)await coordinator;
    else
    {
        try{await coordinator.WaitAsync(TimeSpan.FromSeconds(2));}catch{}
    }
    await discovery.StopAsync(CancellationToken.None);
    discovery.Dispose();
}

async Task RunA22()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a22-offline-idle");
    var health=new MutablePrinterHealthReader(PrinterHealthSnapshots.Fresh(ServiceTestEnvironment.OfflineQueue));
    var transport=new CoordinatorTransport(env.CreateClaimItem(3022),new ConcurrentQueue<string>());
    var processFactory=new CountingNoStartFactory();
    var service=env.CreateService(transport,true,processFactory,health:health,destinations:[ServiceTestEnvironment.TestDestination]);

    for(var i=0;i<20;i++)await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ClaimCount==0,"offline destination suppresses claim API calls across repeated idle coordinator checks");
    Check(transport.AcceptCount==0&&transport.StartCount==0,"offline idle state creates no accept/start churn");
    Check(processFactory.StartCount==0,"offline idle state creates no worker churn");

    health.Set(PrinterHealthSnapshots.Fresh(ServiceTestEnvironment.ReadyQueue));
    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ClaimCount==1,"when cached discovery becomes online/fresh the next owner iteration resumes claim");
    Check(transport.AcceptCount==1&&transport.StartCount==1,"online transition proceeds through accept/start exactly once");
    Check(processFactory.StartCount==1,"online transition reaches one worker launch attempt");
}

async Task RunA23()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a23-printer-freshness");
    var clock=new FakeAgentClock(new DateTimeOffset(2026,9,10,12,0,0,TimeSpan.Zero));
    var state=new PrinterHealthState(clock);
    state.MarkSuccess([ServiceTestEnvironment.ReadyQueue]);
    var first=state.Read(PrinterDiscoveryService.FreshnessWindow);
    var firstSuccess=first.LastSuccessAt;

    clock.Advance(TimeSpan.FromSeconds(5));
    state.MarkFailure("synthetic_discovery_failure");
    var afterFailure=state.Read(PrinterDiscoveryService.FreshnessWindow);
    Check(afterFailure.LastSuccessAt==firstSuccess,"discovery failure does not fabricate a new successful discovery timestamp");
    Check(afterFailure.LastFailureAt==clock.UtcNow,"discovery failure records a separate failure timestamp");
    Check(afterFailure.LastError=="synthetic_discovery_failure","discovery failure reason remains separately observable");
    Check(afterFailure.IsFresh&&afterFailure.AgeMilliseconds is >=4900 and <=5100,"freshness age continues from last success after a failure");

    clock.Advance(TimeSpan.FromSeconds(16));
    var stale=state.Read(PrinterDiscoveryService.FreshnessWindow);
    Check(!stale.IsFresh&&stale.AgeMilliseconds is >20000,"snapshot becomes stale strictly by monotonic age without wall-clock refresh");

    var transport=new CoordinatorTransport(env.CreateClaimItem(3023),new ConcurrentQueue<string>());
    var processFactory=new CountingNoStartFactory();
    var service=env.CreateService(transport,true,processFactory,health:state,destinations:[ServiceTestEnvironment.TestDestination]);
    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ClaimCount==0,"stale printer snapshot is never used as readiness permission");

    await InvokePrivateTaskAsync(service,"WriteLocalHealthAsync","running",true,true,null,CancellationToken.None);
    var healthJson=await File.ReadAllTextAsync(env.Paths.HealthPath);
    var localHealth=JsonSerializer.Deserialize<LocalHealthSnapshot>(healthJson,AgentOptions.JsonOptions());
    Check(localHealth?.PrinterDiscoveryFresh==false,"health.json exposes stale discovery explicitly");
    Check(localHealth?.PrinterDiscoveryLastSuccessAt==firstSuccess?.ToString("O"),"health.json preserves the real last successful discovery time");
    Check(localHealth?.PrinterDiscoveryError=="synthetic_discovery_failure","health.json exposes discovery failure separately from queue state");
    Check(localHealth?.PrinterDiscoveryAgeMilliseconds is >20000,"health.json exposes discovery age instead of a fabricated current timestamp");

    state.MarkSuccess([ServiceTestEnvironment.OfflineQueue]);
    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ClaimCount==0,"fresh-but-offline queue is distinct from stale discovery and still blocks readiness");

    state.MarkSuccess([ServiceTestEnvironment.ReadyQueue]);
    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ClaimCount==1,"fresh online queue restores readiness");
    Check(processFactory.StartCount==1,"restored readiness reaches exactly one worker launch attempt");
}

async Task RunA30()
{
    using var env=await ServiceTestEnvironment.CreateAsync("a30-claim-conflict-rekey");
    var old=env.CreateClaimItem(3030);
    await env.Store.PersistReservedAsync(old,"receipt-a30-old","server-a");
    await env.Store.SetStateAsync(old.Attempt.Id,LocalJobState.Resolved);
    var changedPayload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Replacement\"}";
    var conflict=old with
    {
        Job=old.Job with{Id=9900,PayloadJson=changedPayload,ContentSha256=CryptoUtil.Sha256Hex(changedPayload)},
        Attempt=old.Attempt with{AttemptNo=2,LeaseToken="lease-conflict",LeaseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")}
    };
    var replacement=conflict with{Attempt=conflict.Attempt with{Id=4030,AttemptNo=3,LeaseToken="lease-replacement"}};
    var transport=new ClaimReconciliationTransport(conflict,replacement);
    var processFactory=new CountingNoStartFactory();
    var service=env.CreateService(transport,true,processFactory,claimConflictRekeySupported:true);

    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    var quarantineRaw=await env.Store.GetMetaAsync("pending_claim_state_v2");
    Check(quarantineRaw?.Contains("quarantined",StringComparison.OrdinalIgnoreCase)==true,"identity conflict is durably quarantined before any repair request");
    Check(processFactory.StartCount==0,"conflicting numeric attempt never reaches worker");

    await InvokePrivateAsync(service,"RunCoordinatorWorkAsync");
    Check(transport.ReconciliationCount==1,"same durable conflict invokes one proof-bounded reconciliation mutation");
    var sentResolution=transport.LastResolution;
    Check(sentResolution is not null&&sentResolution.AttemptId==3030&&sentResolution.LocalMaxAttemptId>=3030,"reconciliation carries old identity ceiling without payload or lease");
    Check(await env.Store.GetMetaAsync("pending_claim_state_v2") is null,"replacement replay completes and clears pending claim metadata");
    Check((await env.Store.GetByAttemptAsync(3030))?.State==LocalJobState.Resolved,"old durable attempt remains unchanged");
    Check((await env.Store.GetByAttemptAsync(4030)) is not null,"replacement attempt receives a distinct durable local row");
    Check(processFactory.StartCount==1,"only the distinct replacement attempt may reach the worker path");
}

static bool IsAccept(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=accept",StringComparison.OrdinalIgnoreCase);
static bool IsAttemptStatus(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=attempt_status",StringComparison.OrdinalIgnoreCase);
static bool IsStart(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=start",StringComparison.OrdinalIgnoreCase);
static bool IsProbe(CapturedHttpRequest request)
    =>request.Method=="POST"&&request.Target.Contains("action=probe",StringComparison.OrdinalIgnoreCase);

static bool IsOrdered(string[] events,params string[] expected)
{
    var index=0;
    foreach(var item in events)
    {
        if(index<expected.Length&&item==expected[index])index++;
    }
    return index==expected.Length;
}

static async Task InvokePrivateAsync(PrintAgentService service,string methodName)
    =>await InvokePrivateTaskAsync(service,methodName,CancellationToken.None);

static async Task InvokePrivateTaskAsync(PrintAgentService service,string methodName,params object?[] args)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    var task=method.Invoke(service,args) as Task
        ??throw new InvalidOperationException($"{methodName} did not return Task.");
    await task;
}

static void InvokePrivateVoid(PrintAgentService service,string methodName,params object?[] args)
{
    var method=typeof(PrintAgentService).GetMethod(methodName,BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingMethodException(typeof(PrintAgentService).FullName,methodName);
    _=method.Invoke(service,args);
}

static async Task<bool> TryInvokePrivateAsync(PrintAgentService service,string methodName)
{
    try{await InvokePrivateAsync(service,methodName);return false;}
    catch{return true;}
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
    public static readonly DestinationConfig TestDestination=new("prep","Test","Test Queue",80,72,1,"combined");
    public static readonly PrinterQueueHealth ReadyQueue=new("Test Queue",false,false,false,false,0,"Acceptance Driver","LPT1:");
    public static readonly PrinterQueueHealth OfflineQueue=new("Test Queue",true,false,false,false,0,"Acceptance Driver","LPT1:");

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

    public async Task<LocalQueueStore> CreateRestartedStoreAsync()
    {
        var store=new LocalQueueStore(Paths.DatabasePath,Protector);
        await store.InitializeAsync();
        return store;
    }

    public ClaimItem CreateClaimItem(long attemptId)
    {
        var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"Service Acceptance\"}";
        var hash=CryptoUtil.Sha256Hex(payload);
        return new ClaimItem(
            new ClaimedJob(9000+(int)(attemptId%1000),"pub","prep_order",true,"order","1",DateTimeOffset.UtcNow.ToString("O"),4,hash,payload),
            new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-test",DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
            TestDestination);
    }

    public async Task<LocalJob> CreateJobAsync(long attemptId,string receipt,DateTimeOffset leaseExpiresAt)
    {
        var claim=CreateClaimItem(attemptId) with{Attempt=new ClaimAttempt(attemptId,(int)(attemptId%1000),"lease-test",leaseExpiresAt.ToString("O"))};
        return await Store.PersistReservedAsync(claim,receipt,"server-a");
    }

    public string InputPath(LocalJob job)=>Path.Combine(Paths.WorkPath,$"input-{job.ServerJobId}-{job.AttemptId}.json");

    public PrintAgentService CreateService(
        IPrintTransport transport,
        bool attemptStatusSupported,
        IWorkerProcessFactory processFactory,
        LocalQueueStore? store=null,
        IPrinterHealthReader? health=null,
        IReadOnlyList<DestinationConfig>? destinations=null,
        PrintWakeSignal? wake=null,
        bool claimConflictRekeySupported=false)
    {
        store??=Store;
        health??=new MutablePrinterHealthReader(PrinterHealthSnapshots.Fresh(ReadyQueue));
        wake??=new PrintWakeSignal();
        var dispatcher=new ReportDispatcher(store,new ReportDeliveryPolicy(jitter:()=>0.5),Log);
        var service=new PrintAgentService(
            Paths,
            store,
            health,
            NullLogger<PrintAgentService>.Instance,
            Log,
            wake,
            dispatcher,
            new DurableMutationRequestStore(store),
            new BridgeRuntimeState(),
            new WorkerSupervisor(processFactory));
        SetPrivateField(service,"_api",transport);
        SetPrivateField(service,"_attemptStatusSupported",attemptStatusSupported);
        SetPrivateField(service,"_claimConflictRekeySupported",claimConflictRekeySupported);
        SetPrivateField(service,"_serverScope","server-a");
        SetPrivateField(service,"_boundServerScope","server-a");
        SetPrivateField(service,"_destinations",destinations??new[]{TestDestination});
        SetPrivateField(service,"_configurationGeneration",1L);
        return service;
    }

    private static void SetPrivateField(object instance,string name,object value)
    {
        var field=instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)
            ??throw new MissingFieldException(instance.GetType().FullName,name);
        field.SetValue(instance,value);
    }

    public void Dispose(){try{Directory.Delete(_root,true);}catch{}}
}

static class PrinterHealthSnapshots
{
    public static PrinterHealthSnapshot Fresh(params PrinterQueueHealth[] queues)
        =>new(queues,DateTimeOffset.UtcNow,null,null,0,true,1);
}

sealed class MutablePrinterHealthReader:IPrinterHealthReader
{
    private PrinterHealthSnapshot _snapshot;
    public MutablePrinterHealthReader(PrinterHealthSnapshot snapshot)=>_snapshot=snapshot;
    public void Set(PrinterHealthSnapshot snapshot)=>_snapshot=snapshot;
    public PrinterHealthSnapshot Read(TimeSpan freshnessWindow)=>_snapshot;
}

sealed class FakeAgentClock:IAgentTimeSource
{
    public FakeAgentClock(DateTimeOffset utcNow){UtcNow=utcNow;MonotonicNow=TimeSpan.Zero;}
    public DateTimeOffset UtcNow{get;private set;}
    public TimeSpan MonotonicNow{get;private set;}
    public void Advance(TimeSpan elapsed){UtcNow+=elapsed;MonotonicNow+=elapsed;}
}

sealed class BlockingPrinterProvider:IPrinterHealthProvider,IDisposable
{
    private readonly ManualResetEventSlim _release=new(false);
    private int _calls;
    public int CallCount=>Volatile.Read(ref _calls);
    public TaskCompletionSource<bool> Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<PrinterQueueHealth> GetQueues()
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult(true);
        _release.Wait();
        return [ServiceTestEnvironment.ReadyQueue];
    }
    public void Release()=>_release.Set();
    public void Dispose(){_release.Set();_release.Dispose();}
}

sealed class CountingNoStartFactory:IWorkerProcessFactory
{
    private readonly ConcurrentQueue<string>? _events;
    private int _starts;
    public CountingNoStartFactory(ConcurrentQueue<string>? events=null)=>_events=events;
    public int StartCount=>Volatile.Read(ref _starts);
    public IWorkerProcess Start(WorkerLaunchSpec spec)
    {
        Interlocked.Increment(ref _starts);
        _events?.Enqueue("worker");
        throw new InvalidOperationException("simulated worker launch failure before child ownership");
    }
}

sealed class CoordinatorTransport:IPrintTransport
{
    private readonly ClaimItem? _claim;
    private readonly ConcurrentQueue<string> _events;
    private int _claimDelivered;
    private int _claimCount,_acceptCount,_startCount,_reportCount,_heartbeatCount,_probeCount;

    public CoordinatorTransport(ClaimItem? claim,ConcurrentQueue<string> events){_claim=claim;_events=events;}
    public int ClaimCount=>Volatile.Read(ref _claimCount);
    public int AcceptCount=>Volatile.Read(ref _acceptCount);
    public int StartCount=>Volatile.Read(ref _startCount);
    public int ReportCount=>Volatile.Read(ref _reportCount);
    public int HeartbeatCount=>Volatile.Read(ref _heartbeatCount);
    public int ProbeCount=>Volatile.Read(ref _probeCount);

    public TaskCompletionSource<bool> StartEntered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> HeartbeatEntered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ProbeEntered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool>? StartGate{get;set;}
    public TaskCompletionSource<bool>? HeartbeatGate{get;set;}
    public TaskCompletionSource<bool>? ProbeGate{get;set;}

    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)
    {
        Interlocked.Increment(ref _claimCount);
        _events.Enqueue("claim");
        var jobs=new List<ClaimItem>();
        if(_claim is not null&&Interlocked.Exchange(ref _claimDelivered,1)==0)jobs.Add(_claim);
        return Task.FromResult(new ClaimResponse(true,request.RequestId,jobs,DateTimeOffset.UtcNow.ToString("O"),false));
    }

    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)
    {
        Interlocked.Increment(ref _acceptCount);
        _events.Enqueue("accept");
        return Task.FromResult(new ApiResult(true,"claimed",AttemptId:item.Attempt.Id,JobId:item.Job.Id,LocalReceiptId:localReceiptId,ServerTime:DateTimeOffset.UtcNow.ToString("O")));
    }

    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)
        =>Task.FromResult(new ApiResult(true,"claimed",AttemptId:item.Attempt.Id,JobId:item.Job.Id,ServerTime:DateTimeOffset.UtcNow.ToString("O")));

    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)
        =>Task.FromResult(new AttemptStatusResult(true,job.AttemptId,job.ServerJobId,"claimed","open",true,"start",false,false,job.LeaseExpiresAt.ToString("O"),DateTimeOffset.UtcNow.ToString("O")));

    public async Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)
    {
        Interlocked.Increment(ref _startCount);
        _events.Enqueue("start");
        StartEntered.TrySetResult(true);
        if(StartGate is not null)await StartGate.Task.WaitAsync(ct);
        return new ApiResult(true,"started",AttemptId:job.AttemptId,JobId:job.ServerJobId,LocalReceiptId:job.LocalReceiptId,ServerTime:DateTimeOffset.UtcNow.ToString("O"));
    }

    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)
    {
        Interlocked.Increment(ref _reportCount);
        _events.Enqueue("report");
        return Task.FromResult(new ApiResult(true,request.Status,AttemptId:job.AttemptId,JobId:job.ServerJobId,LocalReceiptId:job.LocalReceiptId));
    }

    public async Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)
    {
        Interlocked.Increment(ref _heartbeatCount);
        _events.Enqueue("heartbeat");
        HeartbeatEntered.TrySetResult(true);
        if(HeartbeatGate is not null)await HeartbeatGate.Task.WaitAsync(ct);
        return new ApiResult(true,"ok");
    }

    public async Task<ProbeResponse> ProbeAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _probeCount);
        _events.Enqueue("probe");
        ProbeEntered.TrySetResult(true);
        if(ProbeGate is not null)await ProbeGate.Task.WaitAsync(ct);
        return new ProbeResponse(true,4,"6.0.0","6.2.0",[ServiceTestEnvironment.TestDestination],["attempt_status"],"acceptance-side-server",DateTimeOffset.UtcNow.ToString("O"));
    }
}

sealed class ClaimReconciliationTransport:IPrintTransport
{
    private readonly ClaimItem _conflict;
    private readonly ClaimItem _replacement;
    private int _claims;
    public int ReconciliationCount{get;private set;}
    public ClaimConflictResolutionRequest? LastResolution{get;private set;}

    public ClaimReconciliationTransport(ClaimItem conflict,ClaimItem replacement){_conflict=conflict;_replacement=replacement;}

    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)
    {
        var item=Interlocked.Increment(ref _claims)==1?_conflict:_replacement;
        return Task.FromResult(new ClaimResponse(true,request.RequestId,[item],DateTimeOffset.UtcNow.ToString("O"),_claims>1));
    }

    public Task<ClaimConflictResolutionResult> ResolveClaimConflictAsync(ClaimConflictResolutionRequest request,CancellationToken ct)
    {
        ReconciliationCount++;LastResolution=request;
        return Task.FromResult(new ClaimConflictResolutionResult(true,"replacement_reserved",request.ClaimRequestId,request.AttemptId,_replacement.Attempt.Id,false,DateTimeOffset.UtcNow.ToString("O")));
    }

    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)
        =>Task.FromResult(new ApiResult(true,"claimed",AttemptId:item.Attempt.Id,JobId:item.Job.Id,LocalReceiptId:localReceiptId));
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,"reserved"));
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>throw new NotSupportedException();
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,"started",AttemptId:job.AttemptId,JobId:job.ServerJobId));
    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ApiResult(true,request.Status));
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)=>Task.FromResult(new ApiResult(true,"ok"));
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>Task.FromResult(new ProbeResponse(true,4,"6.0.0",AgentVersionInfo.Current,[ServiceTestEnvironment.TestDestination],["attempt_status","claim_conflict_rekey_v1"],"acceptance-side-server",DateTimeOffset.UtcNow.ToString("O")));
}

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>SecretStore.ProtectText(value);
    public string Unprotect(string value)=>SecretStore.UnprotectText(value);
}
