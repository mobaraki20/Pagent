using System.Diagnostics;
using System.Text.Json;
using Sokna.PrintAgent.Acceptance;
using Sokna.PrintAgent.Core;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||string.IsNullOrWhiteSpace(caseId) ||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A18 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var assertions=new List<string>();
var failures=new List<string>();
var resultPath=Path.Combine(resultsDirectory,$"{caseId}.result.json");
void Check(bool condition,string name){assertions.Add(name);if(!condition)failures.Add(name);}

if(!string.Equals(caseId,"A18",StringComparison.OrdinalIgnoreCase))
{
    await WriteResult("NOT_RUN",3,$"Contract acceptance case {caseId} is not implemented.");
    return 3;
}

Check(OperatingSystem.IsWindows(),"A18 executes on Windows rather than assuming cross-platform wall-clock behavior");
Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T08:00:00Z",out var utc)&&utc.Offset==TimeSpan.Zero,"UTC Z timestamp accepted");
Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T11:30:00+03:30",out var east)&&east.Offset==TimeSpan.FromHours(3.5),"positive explicit offset accepted");
Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T04:00:00-04:00",out var west)&&west.Offset==TimeSpan.FromHours(-4),"negative explicit offset accepted");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T08:00:00",out _),"offsetless timestamp rejected instead of host-local interpretation");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10 08:00:00Z",out _),"non-contract timestamp without T rejected");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("not-a-time",out _),"malformed timestamp rejected");

var sameInstantA=ApiTimestampPolicy.ParseRequired("2026-09-10T08:00:00Z","server_time");
var sameInstantB=ApiTimestampPolicy.ParseRequired("2026-09-10T11:30:00+03:30","server_time");
Check(sameInstantA==sameInstantB,"explicit offsets normalize to the same instant independent of Windows host timezone");

var fakeClock=new FakeAgentTimeSource(
    new DateTimeOffset(2026,9,10,8,0,0,TimeSpan.Zero),
    TimeSpan.FromSeconds(100));
var anchor=new ServerTimeAnchor(fakeClock);
anchor.Observe("2026-09-10T08:00:00Z");
var anchored=anchor.EstimatedServerNow;
fakeClock.JumpWall(TimeSpan.FromHours(12));
Check(anchor.EstimatedServerNow==anchored,"forward Windows wall-clock jump cannot advance anchored authoritative time");
fakeClock.JumpWall(TimeSpan.FromHours(-36));
Check(anchor.EstimatedServerNow==anchored,"backward Windows wall-clock jump cannot rewind anchored authoritative time");
var deadline=anchored.AddSeconds(20);
Check(!anchor.IsPast(deadline),"authoritative deadline is not expired before monotonic elapsed time reaches it");
fakeClock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
Check(anchor.EstimatedServerNow==anchored.AddSeconds(30),"anchored server time advances only by monotonic elapsed time");
Check(anchor.IsPast(deadline),"authoritative deadline becomes past only after monotonic elapsed time advances");

await using(var server=new LoopbackPrintApiServer([
    LoopbackResponseKind.AttemptStatusClaimed,
    LoopbackResponseKind.AttemptStatusOffsetless]))
using(var http=new HttpClient())
{
    var protector=new DpapiLeaseTokenProtector();
    var job=new LocalJob(
        9018,3018,1,"prep","Test Queue",80,72,1,"combined","{}",CryptoUtil.Sha256Hex("{}"),"receipt-a18",
        protector.Protect("lease-a18"),DateTimeOffset.UtcNow.AddMinutes(5),LocalJobState.Reserved,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null,null,"server-a");
    var transport=new HttpPrintTransport(http,server.BaseUrl,"acceptance-token",protector);
    var valid=await transport.AttemptStatusAsync(job,CancellationToken.None);
    Check(valid.Success&&valid.AttemptId==job.AttemptId&&valid.ReceiptMatches,"real HttpPrintTransport accepts explicitly-offset authoritative status");
    string? code=null;
    try{_ = await transport.AttemptStatusAsync(job,CancellationToken.None);}
    catch(PrintProtocolException e){code=e.Code;}
    Check(code=="timestamp_offset_required","real HttpPrintTransport rejects offsetless status before Service can continue");
    Check(server.Requests.Count==2&&server.Requests.All(r=>r.Target.Contains("action=attempt_status",StringComparison.OrdinalIgnoreCase)),"A18 timestamp contract is exercised through production attempt_status transport");
}

if(failures.Count>0)
{
    await WriteResult("FAIL",1,string.Join(",",failures));
    return 1;
}
await WriteResult("PASS",0,null);
return 0;

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

sealed class FakeAgentTimeSource:IAgentTimeSource
{
    public DateTimeOffset UtcNow{get;private set;}
    public TimeSpan MonotonicNow{get;private set;}

    public FakeAgentTimeSource(DateTimeOffset utcNow,TimeSpan monotonicNow)
    {
        UtcNow=utcNow;
        MonotonicNow=monotonicNow;
    }

    public void JumpWall(TimeSpan delta)=>UtcNow+=delta;
    public void AdvanceMonotonic(TimeSpan delta)=>MonotonicNow+=delta;
}
