using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sokna.PrintAgent.Core;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A53",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A53 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A53.log");
var resultPath=Path.Combine(resultsDirectory,"A53.result.json");
var assertions=new List<string>();
var failures=new List<string>();
var evidence=new List<string>();
string? serverInstanceId=null;
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

var serverUrl=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_SERVER_URL")?.Trim();
var tokenFile=Environment.GetEnvironmentVariable("SOKNA_ACCEPTANCE_TOKEN_FILE")?.Trim();
if(string.IsNullOrWhiteSpace(serverUrl)||string.IsNullOrWhiteSpace(tokenFile)||!File.Exists(tokenFile))
{
    const string blocker="A53 requires SOKNA_ACCEPTANCE_SERVER_URL and SOKNA_ACCEPTANCE_TOKEN_FILE. It performs Probe/Heartbeat only and must target the real acceptance server; loopback/mock cannot PASS.";
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

try
{
    Check(OperatingSystem.IsWindows(),"real heartbeat contract gate executes on Windows");
    using var http=new HttpClient();
    var transport=new HttpPrintTransport(http,serverUrl,token,new DpapiLeaseTokenProtector());
    var probe=await transport.ProbeAsync(CancellationToken.None);
    Check(probe.Success&&probe.ProtocolVersion==4,"real server probe reports successful protocol v4");
    serverInstanceId=probe.ServerInstanceId;
    evidence.Add($"server_instance_id={Safe(serverInstanceId??"(not-advertised)")}");
    evidence.Add($"recommended_agent_version={Safe(probe.RecommendedAgentVersion)}");

    // Agent wire: optional nulls must be omitted while all non-null diagnostics remain present.
    var omitted=new HeartbeatPayload(
        CryptoUtil.NewRequestId(),Environment.MachineName,AgentVersionInfo.Current,Environment.OSVersion.VersionString,10,
        null,0,0,null,"ok",1024,true,true,true,[],
        LastSuccessfulAction:null,LastApiSuccessAt:null,LastApiErrorCode:null,ConsecutiveApiFailures:0,LastApiLatencyMs:null,
        PrinterDiscoveryAt:DateTimeOffset.UtcNow.ToString("O"),BridgeProtocolVersion:1,BridgePort:17653,BridgePairingId:null,BridgeOrigin:null,
        PendingReportCount:0,AuthBlockedReportCount:0,ReconciliationReportCount:0,
        PrinterDiscoveryLastFailureAt:null,PrinterDiscoveryError:null,PrinterDiscoveryAgeMilliseconds:0,PrinterDiscoveryFresh:true,PrinterDiscoveryGeneration:1);
    var omittedResult=await transport.HeartbeatAsync(omitted,CancellationToken.None);
    Check(omittedResult.Success,"real heartbeat accepts Agent wire with optional nulls omitted");

    // Server compatibility: explicit JSON null for allowlisted optional evidence must also be accepted.
    using var raw=new HttpClient();
    raw.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
    var endpoint=$"{serverUrl.TrimEnd('/')}/print-agent/v4/api.php?action=heartbeat";
    var explicitNullPayload=new Dictionary<string,object?>
    {
        ["request_id"]=CryptoUtil.NewRequestId(),["agent_version"]=AgentVersionInfo.Current,["protocol_version"]=4,
        ["hostname"]=Environment.MachineName,["os_version"]=Environment.OSVersion.VersionString,["uptime_seconds"]=11L,
        ["last_poll_success_at"]=null,["local_backlog_count"]=0,["local_unknown_count"]=0,["last_submission_at"]=null,
        ["sqlite_health"]="ok",["disk_free_mb"]=1024L,["worker_ok"]=true,["config_ok"]=true,["instance_lock_ok"]=true,
        ["printers"]=Array.Empty<object>(),["last_successful_action"]=null,["last_api_success_at"]=null,["last_api_error_code"]=null,
        ["consecutive_api_failures"]=0,["last_api_latency_ms"]=null,["printer_discovery_at"]=DateTimeOffset.UtcNow.ToString("O"),
        ["bridge_protocol_version"]=1,["bridge_port"]=17653,["bridge_pairing_id"]=null,["bridge_origin"]=null,
        ["pending_report_count"]=0,["auth_blocked_report_count"]=0,["reconciliation_report_count"]=0,
        ["printer_discovery_last_failure_at"]=null,["printer_discovery_error"]=null,["printer_discovery_age_milliseconds"]=0L,
        ["printer_discovery_fresh"]=true,["printer_discovery_generation"]=2L
    };
    using(var explicitNullResponse=await raw.PostAsJsonAsync(endpoint,explicitNullPayload,AgentOptions.JsonOptions()))
    {
        var body=await explicitNullResponse.Content.ReadAsStringAsync();
        Check(explicitNullResponse.IsSuccessStatusCode,"real server accepts explicit null for optional heartbeat fields");
        evidence.Add($"explicit_null_http={(int)explicitNullResponse.StatusCode}");
        if(!explicitNullResponse.IsSuccessStatusCode)Log("explicit-null response: "+Safe(body));
    }

    // Bridge disabled projection is represented by null pairing/origin on model; Agent transport omits them.
    var bridgeDisabled=omitted with{RequestId=CryptoUtil.NewRequestId(),BridgeProtocolVersion=0,BridgePort=0,BridgePairingId=null,BridgeOrigin=null};
    Check((await transport.HeartbeatAsync(bridgeDisabled,CancellationToken.None)).Success,"real heartbeat accepts Bridge-disabled Agent payload");

    // Wrong scalar type must fail closed with exact field evidence.
    var wrongType=new Dictionary<string,object?>(explicitNullPayload)
    {
        ["request_id"]=CryptoUtil.NewRequestId(),
        ["bridge_origin"]=123
    };
    using(var wrongResponse=await raw.PostAsJsonAsync(endpoint,wrongType,AgentOptions.JsonOptions()))
    {
        var body=await wrongResponse.Content.ReadAsStringAsync();
        string? code=null,field=null;
        try
        {
            using var doc=JsonDocument.Parse(body);
            var root=doc.RootElement;
            if(root.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String)code=c.GetString();
            if(root.TryGetProperty("field",out var f)&&f.ValueKind==JsonValueKind.String)field=f.GetString();
        }
        catch(JsonException){}
        Check(wrongResponse.StatusCode==HttpStatusCode.UnprocessableEntity&&code=="invalid_field_type"&&field=="bridge_origin","wrong heartbeat scalar type returns 422 with exact field");
        evidence.Add($"wrong_type_http={(int)wrongResponse.StatusCode};code={Safe(code??"null")};field={Safe(field??"null")}");
    }

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(',',failures));
        await WriteResult("FAIL",1,string.Join(',',failures));
        return 1;
    }
    Log($"PASS A53; assertions={assertions.Count}; server_instance={Safe(serverInstanceId??"unknown")}");
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

async Task WriteResult(string status,int exitCode,string? blockerOrError)
{
    var payload=new
    {
        case_id="A53",status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real acceptance API; no print job mutation",
        exit_code=exitCode,test_name="A53 real heartbeat contract",test_path="tests/Sokna.PrintAgent.HeartbeatRealApiIntegration/Program.cs",
        command="./scripts/Test-Agent-Acceptance.ps1 -CaseId A53 -ResultsDirectory <dir>",assertions,failed_assertions=failures,evidence,server_instance_id=serverInstanceId,
        raw_log=Path.GetFileName(logPath),blocker_or_error=blockerOrError
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
        var text=process.StandardOutput.ReadToEnd().Trim();process.WaitForExit(5000);
        return process.ExitCode==0&&!string.IsNullOrWhiteSpace(text)?text:"unknown";
    }
    catch{return "unknown";}
}
