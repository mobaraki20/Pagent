using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A34",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A34 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A34.log");
var resultPath=Path.Combine(resultsDirectory,"A34.result.json");
var assertions=new List<string>();
var failures=new List<string>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    Check(OperatingSystem.IsWindows(),"A34 executes on Windows HttpListener semantics");
    using var env=BridgeEnvironment.Create();
    var port=FreePort();
    const string origin="https://secure-cashier.example";
    env.SaveOptions(port,origin,true);
    var pairing=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    await WaitAsync(()=>env.Runtime.Snapshot.Listening&&env.Runtime.Snapshot.Port==port,TimeSpan.FromSeconds(6),"bridge listener");

    var missingOrigin=await SendAsync(port,null,pairing,HttpMethod.Post,"/v1/wake",WakeJson("a34-missing-origin"));
    var wrongOrigin=await SendAsync(port,"https://evil.example",pairing,HttpMethod.Post,"/v1/wake",WakeJson("a34-wrong-origin"));
    var wrongPair=await SendAsync(port,origin,"deadbeefdeadbeefdeadbeef",HttpMethod.Post,"/v1/wake",WakeJson("a34-wrong-pair"));
    Check(missingOrigin.StatusCode==HttpStatusCode.Forbidden&&wrongOrigin.StatusCode==HttpStatusCode.Forbidden,"missing/wrong Origin is rejected");
    Check(wrongPair.StatusCode==HttpStatusCode.Forbidden,"wrong pairing is rejected");

    var get=await SendAsync(port,origin,pairing,HttpMethod.Get,"/v1/wake",null);
    var wrongType=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson("a34-content-type"),"text/plain");
    Check(get.StatusCode==HttpStatusCode.MethodNotAllowed,"non-POST bridge method is rejected");
    Check((int)wrongType.StatusCode==415,"non-JSON content-type is rejected");

    var malformed=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake","{");
    var wrongJsonTypes=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake","{\"type\":7,\"protocol_version\":\"1\",\"request_id\":{},\"job_ids\":\"x\",\"expires_at\":false}");
    Check(malformed.StatusCode==HttpStatusCode.BadRequest,"malformed JSON returns controlled 400");
    Check((int)wrongJsonTypes.StatusCode==422,"wrong JSON types return controlled 422 instead of 500");

    var expired=JsonSerializer.Serialize(new{type="print.wake",protocol_version=1,request_id="a34-expired-0001",job_ids=new[]{1},expires_at=DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")});
    var tooFuture=JsonSerializer.Serialize(new{type="print.wake",protocol_version=1,request_id="a34-future-00001",job_ids=new[]{1},expires_at=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")});
    Check((int)(await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",expired)).StatusCode==422,"expired wake is rejected by TTL policy");
    Check((int)(await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",tooFuture)).StatusCode==422,"far-future wake is rejected by TTL policy");

    const string replayId="a34-replay-000001";
    var replayBody=WakeJson(replayId);
    var first=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",replayBody);
    var second=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",replayBody);
    var secondBody=await second.Content.ReadAsStringAsync();
    using(var replayJson=JsonDocument.Parse(secondBody))
    {
        var root=replayJson.RootElement;
        Check(first.StatusCode==HttpStatusCode.OK&&second.StatusCode==HttpStatusCode.OK,"valid wake and byte-identical replay receive controlled success");
        Check(root.TryGetProperty("success",out var success)&&success.ValueKind==JsonValueKind.True,"replay response is a semantic success");
        Check(root.TryGetProperty("idempotent",out var idempotent)&&idempotent.ValueKind==JsonValueKind.True,"byte-identical replay is idempotently suppressed independent of JSON formatting");
    }
    Log("A34 replay response: "+SafeLogText.Sanitize(secondBody,300));

    var oversized=new string('x',9000);
    var tooLarge=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",oversized);
    Check(tooLarge.StatusCode==HttpStatusCode.RequestEntityTooLarge,"oversized bridge body is rejected before JSON processing");

    var invalidPreview="{\"type\":\"print.preview\",\"protocol_version\":1,\"revision\":\"bad\",\"payload_json\":7,\"paper_width_mm\":80,\"printable_width_mm\":72}";
    var preview=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/preview",invalidPreview);
    Check((int)preview.StatusCode==422,"invalid preview types are rejected before Worker launch");

    Check(env.Runtime.Snapshot.Listening,"listener remains healthy after rejected hostile/malformed requests");

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }
    Log($"PASS A34; assertions={assertions.Count}");
    await WriteResult("PASS",0,null);
    return 0;
}
catch(Exception e)
{
    var error=$"{e.GetType().Name}: {SafeLogText.Sanitize(e.Message,400)}";
    Log("FAIL "+error);
    await WriteResult("FAIL",1,error);
    return 1;
}

async Task WriteResult(string status,int exitCode,string? error)
{
    var payload=new
    {
        case_id="A34",status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real HttpListener",
        exit_code=exitCode,
        test_name="A34 Local Bridge origin/pairing/replay/TTL/method/content-type/schema/size security",
        test_path="tests/Sokna.PrintAgent.BridgeSecurityAcceptance/Program.cs",
        command="dotnet run --project tests/Sokna.PrintAgent.BridgeSecurityAcceptance/Sokna.PrintAgent.BridgeSecurityAcceptance.csproj -c Release --no-build -- --case A34 --results <dir>",
        assertions,failed_assertions=failures,raw_log=Path.GetFileName(logPath),error
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
}

static string WakeJson(string requestId)=>JsonSerializer.Serialize(new
{
    type="print.wake",protocol_version=1,request_id=requestId,job_ids=new[]{101},expires_at=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")
});

static async Task<HttpResponseMessage> SendAsync(int port,string? origin,string pairing,HttpMethod method,string path,string? body,string contentType="application/json")
{
    using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};
    using var request=new HttpRequestMessage(method,$"http://127.0.0.1:{port}{path}");
    if(origin is not null)request.Headers.TryAddWithoutValidation("Origin",origin);
    request.Headers.TryAddWithoutValidation("X-Sokna-Bridge-Pairing",pairing);
    if(body is not null)request.Content=new StringContent(body,Encoding.UTF8,contentType);
    return await client.SendAsync(request,HttpCompletionOption.ResponseContentRead);
}

static int FreePort()
{
    var listener=new TcpListener(IPAddress.Loopback,0);
    listener.Start();
    var port=((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitAsync(Func<bool> condition,TimeSpan timeout,string label)
{
    var deadline=DateTimeOffset.UtcNow+timeout;
    while(DateTimeOffset.UtcNow<deadline)
    {
        if(condition())return;
        await Task.Delay(40);
    }
    throw new TimeoutException("Timed out waiting for "+label+".");
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

sealed class BridgeEnvironment:IDisposable
{
    private readonly string _root;
    private LocalBridgeService? _service;
    public AgentPaths Paths{get;}
    public PrintWakeSignal Wake{get;}=new();
    public BridgeRuntimeState Runtime{get;}=new();

    private BridgeEnvironment(string root)
    {
        _root=root;
        Paths=new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
    }

    public static BridgeEnvironment Create()=>new(Path.Combine(Path.GetTempPath(),$"sokna-bridge-security-{Guid.NewGuid():N}"));

    public void SaveOptions(int port,string origin,bool enabled)
    {
        new AgentOptions{ServerBaseUrl=origin,RequireHttps=true,LocalBridgeEnabled=enabled,LocalBridgePort=port,LocalBridgeAllowedOrigin=origin}.Save(Paths.ConfigPath);
    }

    public async Task StartAsync()
    {
        _service=new LocalBridgeService(Paths,Wake,Runtime);
        await _service.StartAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if(_service is not null)
        {
            try{_service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();}catch{}
            _service.Dispose();
        }
        try{Directory.Delete(_root,true);}catch{}
    }
}
