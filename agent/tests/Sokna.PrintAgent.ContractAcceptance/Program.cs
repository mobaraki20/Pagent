using System.Diagnostics;
using System.Text.Json;
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

Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T08:00:00Z",out var utc)&&utc.Offset==TimeSpan.Zero,"UTC Z timestamp accepted");
Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T11:30:00+03:30",out var east)&&east.Offset==TimeSpan.FromHours(3.5),"positive explicit offset accepted");
Check(ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T04:00:00-04:00",out var west)&&west.Offset==TimeSpan.FromHours(-4),"negative explicit offset accepted");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10T08:00:00",out _),"offsetless timestamp rejected instead of host-local interpretation");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("2026-09-10 08:00:00Z",out _),"non-contract timestamp without T rejected");
Check(!ApiTimestampPolicy.TryParseExplicitOffset("not-a-time",out _),"malformed timestamp rejected");

var sameInstantA=ApiTimestampPolicy.ParseRequired("2026-09-10T08:00:00Z","server_time");
var sameInstantB=ApiTimestampPolicy.ParseRequired("2026-09-10T11:30:00+03:30","server_time");
Check(sameInstantA==sameInstantB,"explicit offsets normalize to the same instant independent of Windows host timezone");

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
