using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Sokna.PrintAgent.Core;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A49",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A49 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A49.log");
var resultPath=Path.Combine(resultsDirectory,"A49.result.json");
var assertions=new List<string>();
var failures=new List<string>();
var evidence=new List<string>();
string? serverInstanceId=null;
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

var serverUrl=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_SERVER_URL")?.Trim();
var proxyUrl=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_FAULT_PROXY_URL")?.Trim();
var tokenFile=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_TOKEN_FILE")?.Trim();
var destinationKey=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_DESTINATION_KEY")?.Trim();
var mutationGuard=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_ALLOW_MUTATION");

if(string.IsNullOrWhiteSpace(serverUrl)||string.IsNullOrWhiteSpace(proxyUrl)||string.IsNullOrWhiteSpace(tokenFile)||
   string.IsNullOrWhiteSpace(destinationKey)||!File.Exists(tokenFile)||
   !string.Equals(mutationGuard,"I_UNDERSTAND_THIS_MUTATES_ACCEPTANCE_API",StringComparison.Ordinal))
{
    const string blocker="A49 requires SOKNA_ACCEPTANCE_SERVER_URL, SOKNA_ACCEPTANCE_FAULT_PROXY_URL, SOKNA_ACCEPTANCE_TOKEN_FILE, SOKNA_ACCEPTANCE_DESTINATION_KEY and SOKNA_ACCEPTANCE_ALLOW_MUTATION=I_UNDERSTAND_THIS_MUTATES_ACCEPTANCE_API. The proxy must drop a response only after upstream commit when X-Sokna-Acceptance-Drop-Response=after-commit is present.";
    await WriteResult("NOT_RUN",3,blocker);
    Console.Error.WriteLine(blocker);
    return 3;
}

var token=(await File.ReadAllTextAsync(tokenFile)).Trim();
if(string.IsNullOrWhiteSpace(token))
{
    await WriteResult("NOT_RUN",3,"Acceptance token file is empty.");
    return 3;
}

var tempRoot=Path.Combine(Path.GetTempPath(),$"sokna-a49-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
try
{
    Check(OperatingSystem.IsWindows(),"real API integration gate executes on Windows");
    using var directHttp=new HttpClient();
    using var proxyHttp=new HttpClient();
    var protector=new DpapiLeaseTokenProtector();
    var direct=new HttpPrintTransport(directHttp,serverUrl,token,protector);
    var faultProxy=new HttpPrintTransport(proxyHttp,proxyUrl,token,protector);

    var probe=await direct.ProbeAsync(CancellationToken.None);
    Check(probe.Success&&probe.ProtocolVersion==4,"real server probe reports successful protocol v4");
    Check(probe.Capabilities?.Contains("attempt_status",StringComparer.OrdinalIgnoreCase)==true,"real server advertises attempt_status capability");
    Check(probe.Destinations.Any(x=>string.Equals(x.DestinationKey,destinationKey,StringComparison.OrdinalIgnoreCase)),"configured acceptance destination is advertised by real server");
    serverInstanceId=probe.ServerInstanceId;
    evidence.Add($"server_instance_id={Safe(serverInstanceId??"(not-advertised)")}");
    evidence.Add($"minimum_agent_version={Safe(probe.MinimumAgentVersion)}");
    evidence.Add($"recommended_agent_version={Safe(probe.RecommendedAgentVersion)}");

    var proxyProbe=await faultProxy.ProbeAsync(CancellationToken.None);
    Check(proxyProbe.Success&&proxyProbe.ProtocolVersion==4,"fault proxy reaches the same real protocol surface before fault injection");
    if(!string.IsNullOrWhiteSpace(probe.ServerInstanceId)&&!string.IsNullOrWhiteSpace(proxyProbe.ServerInstanceId))
        Check(string.Equals(probe.ServerInstanceId,proxyProbe.ServerInstanceId,StringComparison.Ordinal),"direct and fault-proxy endpoints identify the same server instance");

    var claimRequest=new ClaimRequestEnvelope(
        CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,[destinationKey],1,DateTimeOffset.UtcNow.ToString("O"));
    var claim=await direct.ClaimAsync(claimRequest,CancellationToken.None);
    Check(claim.Success,"real Claim succeeds");
    Check(string.Equals(claim.RequestId,claimRequest.RequestId,StringComparison.Ordinal),"real Claim echoes the durable request identity");
    if(claim.Jobs.Count==0)
    {
        const string blocker="Real API is reachable but no seeded acceptance print job was claimable for SOKNA_ACCEPTANCE_DESTINATION_KEY. Seed a disposable acceptance job and rerun A49.";
        Log(blocker);
        await WriteResult("NOT_RUN",3,blocker);
        return 3;
    }

    var item=claim.Jobs[0];
    Check(string.Equals(item.Destination.DestinationKey,destinationKey,StringComparison.OrdinalIgnoreCase),"real Claim returns the requested acceptance destination");
    var store=new LocalQueueStore(Path.Combine(tempRoot,"queue.db"),protector);
    await store.InitializeAsync();
    var receipt=CryptoUtil.NewLocalReceiptId();
    var local=await store.PersistReservedAsync(item,receipt,$"a49:{probe.ServerInstanceId??new Uri(serverUrl).Authority}");

    var acceptRequest=new AcceptRequestEnvelope(
        CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,local.AttemptId,local.LocalReceiptId,local.ContentSha256);
    var responseLost=false;
    proxyHttp.DefaultRequestHeaders.Add("X-Sokna-Acceptance-Drop-Response","after-commit");
    try
    {
        _=await faultProxy.AcceptAsync(item,acceptRequest,CancellationToken.None);
    }
    catch(Exception e) when(e is HttpRequestException or TaskCanceledException)
    {
        responseLost=true;
        Log($"Expected after-commit response loss observed: {e.GetType().Name}");
    }
    finally
    {
        proxyHttp.DefaultRequestHeaders.Remove("X-Sokna-Acceptance-Drop-Response");
    }
    Check(responseLost,"external fault proxy actually drops the Accept response after forwarding it upstream");

    var reconciled=await direct.AttemptStatusAsync(local,CancellationToken.None);
    Check(reconciled.Success&&reconciled.AttemptId==local.AttemptId&&reconciled.JobId==local.ServerJobId,"AttemptStatus resolves the same real attempt after the lost Accept response");
    Check(reconciled.ReceiptMatches&&!reconciled.RequiresHumanResolution,"AttemptStatus binds the local receipt and grants non-human continuation after confirmed commit");
    Check(reconciled.AttemptState is "claimed" or "started","real server confirms Accept committed despite the lost response");

    var acceptReplay=await direct.AcceptAsync(item,acceptRequest,CancellationToken.None);
    Check(acceptReplay.Success,"replay of the exact same Accept request is idempotently accepted by the real server");
    await store.SetStateAsync(local.AttemptId,LocalJobState.Claimed);
    local=(await store.GetByAttemptAsync(local.AttemptId))!;

    var startRequest=new StartRequestEnvelope(CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,local.AttemptId);
    var startResult=await direct.StartAsync(local,startRequest,CancellationToken.None);
    Check(startResult.Success,"real Start succeeds for the reconciled accepted attempt");
    if(startResult.AttemptId is { } startAttempt)Check(startAttempt==local.AttemptId,"real Start response preserves attempt identity");

    var afterStart=await direct.AttemptStatusAsync(local,CancellationToken.None);
    Check(afterStart.Success&&afterStart.AttemptId==local.AttemptId&&afterStart.JobId==local.ServerJobId,"AttemptStatus resolves the same attempt after Start");
    Check(afterStart.ReceiptMatches&&!afterStart.RequiresHumanResolution,"post-Start status preserves receipt binding without invented human resolution");

    var reportRequest=new ReportRequestEnvelope(
        CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,local.AttemptId,local.LocalReceiptId,
        "failed",null,false,"acceptance_contract_probe","A49 controlled acceptance result; no local Worker or Spooler submission was performed.");
    var reportResult=await direct.ReportAsync(local,reportRequest,CancellationToken.None);
    Check(reportResult.Success,"real Report accepts the controlled non-spooled acceptance outcome");
    if(reportResult.AttemptId is { } reportAttempt)Check(reportAttempt==local.AttemptId,"real Report response preserves attempt identity");
    if(!string.IsNullOrWhiteSpace(reportResult.LocalReceiptId))Check(reportResult.LocalReceiptId==local.LocalReceiptId,"real Report response preserves receipt identity");

    var finalStatus=await direct.AttemptStatusAsync(local,CancellationToken.None);
    Check(finalStatus.Success&&finalStatus.AttemptId==local.AttemptId&&finalStatus.JobId==local.ServerJobId,"AttemptStatus remains authoritative after the final Report");
    Check(finalStatus.ReceiptMatches,"final authoritative status remains bound to the same local receipt");
    Check(finalStatus.Terminal||finalStatus.NextAction is "stop" or "none" or "report","real server exposes a non-print continuation after the controlled final report");

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }

    evidence.Add($"attempt_id={local.AttemptId}");
    evidence.Add($"job_id={local.ServerJobId}");
    evidence.Add($"accept_request_id={acceptRequest.RequestId}");
    evidence.Add($"start_request_id={startRequest.RequestId}");
    evidence.Add($"report_request_id={reportRequest.RequestId}");
    Log($"PASS A49; assertions={assertions.Count}; server_instance={Safe(serverInstanceId??"unknown")}");
    await WriteResult("PASS",0,null);
    return 0;
}
catch(Exception e)
{
    var error=$"{e.GetType().Name}: {Safe(e.Message)}";
    Log("FAIL "+error);
    await WriteResult("FAIL",1,error);
    return 1;
}
finally
{
    try{Directory.Delete(tempRoot,true);}catch{}
}

async Task WriteResult(string status,int exitCode,string? blockerOrError)
{
    var payload=new
    {
        case_id="A49",
        status,
        source_sha=ResolveSourceSha(),
        run_started_at=started.ToString("O"),
        run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real API + explicit after-commit fault proxy",
        exit_code=exitCode,
        test_name="A49 real Print API v4 contract and lost-response reconciliation",
        test_path="tests/Sokna.PrintAgent.RealApiIntegration/Program.cs",
        command="./scripts/Test-Agent-Acceptance.ps1 -Suite Integration -ResultsDirectory <dir>",
        assertions,
        failed_assertions=failures,
        evidence,
        server_instance_id=serverInstanceId,
        raw_log=Path.GetFileName(logPath),
        blocker_or_error=blockerOrError
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
}

static string Safe(string value)=>SafeLogText.Sanitize(value,300);

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
