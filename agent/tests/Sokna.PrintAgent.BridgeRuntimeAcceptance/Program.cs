using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A32",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A32 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A32.log");
var resultPath=Path.Combine(resultsDirectory,"A32.result.json");
var assertions=new List<string>();
var failures=new List<string>();
var evidence=new List<object>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    Check(OperatingSystem.IsWindows(),"A32 executes on Windows HttpListener semantics");
    using var env=BridgeEnvironment.Create();
    var port1=FreePort();
    var port2=FreePort();
    while(port2==port1)port2=FreePort();
    const string origin1="https://cashier-a.example";
    const string origin2="https://cashier-b.example";

    env.SaveOptions(port1,origin1,true);
    var pairing1=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    var first=await WaitListeningAsync(env.Runtime,port1,TimeSpan.FromSeconds(6));
    var firstHeartbeat=BridgeHeartbeatProjection.From(first);
    evidence.Add(new{stage="initial",runtime=first,heartbeat=firstHeartbeat});
    Check(first.Listening&&first.Enabled&&first.Port==port1&&first.Origin==origin1,"initial bridge generation listens on configured port/origin");
    Check(firstHeartbeat.ProtocolVersion==1&&firstHeartbeat.Port==port1&&firstHeartbeat.Origin==origin1&&firstHeartbeat.PairingId==pairing1,"heartbeat projection advertises the actual initial listener generation");
    Check((await PostWakeAsync(port1,origin1,pairing1,"a32-first-0001")).StatusCode==HttpStatusCode.OK,"initial generation accepts valid wake");

    env.SaveOptions(port2,origin2,true);
    var second=await WaitGenerationAsync(env.Runtime,first.Generation,port2,TimeSpan.FromSeconds(8));
    var secondHeartbeat=BridgeHeartbeatProjection.From(second);
    evidence.Add(new{stage="config_reload",runtime=second,heartbeat=secondHeartbeat});
    Check(second.Generation>first.Generation&&second.Listening&&second.Port==port2&&second.Origin==origin2,"config change replaces listener generation without service restart");
    Check(!await CanConnectAsync(port1),"old bridge port no longer accepts after reload");
    Check(secondHeartbeat.ProtocolVersion==1&&secondHeartbeat.Port==port2&&secondHeartbeat.Origin==origin2&&secondHeartbeat.PairingId==pairing1,"heartbeat projection follows the reloaded listener rather than stale config state");
    Check((await PostWakeAsync(port2,origin2,pairing1,"a32-second-0002")).StatusCode==HttpStatusCode.OK,"reloaded generation accepts valid wake");
    Check((await PostWakeAsync(port2,origin1,pairing1,"a32-old-origin")).StatusCode==HttpStatusCode.Forbidden,"old origin is rejected after reload");

    var pairing2=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    File.WriteAllText(LocalBridgeService.PairingPath(env.Paths),pairing2,Encoding.UTF8);
    var third=await WaitGenerationAsync(env.Runtime,second.Generation,port2,TimeSpan.FromSeconds(8));
    var thirdHeartbeat=BridgeHeartbeatProjection.From(third);
    evidence.Add(new{stage="pairing_rotation",runtime=third,heartbeat=thirdHeartbeat});
    Check(third.Generation>second.Generation&&third.Listening,"pairing rotation creates a new live listener generation");
    Check(thirdHeartbeat.PairingId==pairing2&&thirdHeartbeat.Port==port2&&thirdHeartbeat.ProtocolVersion==1,"heartbeat projection exposes only the active rotated pairing");
    Check((await PostWakeAsync(port2,origin2,pairing1,"a32-old-pair")).StatusCode==HttpStatusCode.Forbidden,"old pairing is rejected after rotation");
    Check((await PostWakeAsync(port2,origin2,pairing2,"a32-new-pair")).StatusCode==HttpStatusCode.OK,"rotated pairing is accepted without service restart");

    env.SaveOptions(port2,origin2,false);
    await WaitAsync(()=>!env.Runtime.Snapshot.Enabled&&!env.Runtime.Snapshot.Listening,TimeSpan.FromSeconds(8),"bridge disable");
    var disabled=env.Runtime.Snapshot;
    var disabledHeartbeat=BridgeHeartbeatProjection.From(disabled);
    evidence.Add(new{stage="disabled",runtime=disabled,heartbeat=disabledHeartbeat});
    Check(!await CanConnectAsync(port2),"disabled bridge accepts no new connections");
    Check(disabledHeartbeat.ProtocolVersion==0&&disabledHeartbeat.Port==0&&disabledHeartbeat.PairingId is null&&disabledHeartbeat.Origin is null,"heartbeat advertises no bridge when runtime is disabled");

    var occupiedPort=FreePort();
    using(var occupied=new TcpListener(IPAddress.Loopback,occupiedPort))
    {
        occupied.Start();
        env.SaveOptions(occupiedPort,origin2,true);
        await WaitAsync(()=>env.Runtime.Snapshot.ErrorCode=="bind_failed",TimeSpan.FromSeconds(8),"bind failure runtime state");
        var failed=env.Runtime.Snapshot;
        var failedHeartbeat=BridgeHeartbeatProjection.From(failed);
        evidence.Add(new{stage="bind_failed",runtime=failed,heartbeat=failedHeartbeat});
        Check(failed.Enabled&&!failed.Listening&&failed.ErrorCode=="bind_failed","occupied port is represented as enabled-but-not-listening runtime failure");
        Check(failedHeartbeat.ProtocolVersion==0&&failedHeartbeat.Port==0&&failedHeartbeat.PairingId is null&&failedHeartbeat.Origin is null,"heartbeat never advertises configured-but-dead bridge listener");
    }

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }

    Log($"PASS A32; assertions={assertions.Count}");
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
        case_id="A32",status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real HttpListener + BridgeRuntimeState",
        exit_code=exitCode,
        test_name="A32 live bridge reload/rotation/disable/bind-failure heartbeat truth",
        test_path="tests/Sokna.PrintAgent.BridgeRuntimeAcceptance/Program.cs",
        command="dotnet run --project tests/Sokna.PrintAgent.BridgeRuntimeAcceptance/Sokna.PrintAgent.BridgeRuntimeAcceptance.csproj -c Release --no-build -- --case A32 --results <dir>",
        assertions,failed_assertions=failures,evidence,raw_log=Path.GetFileName(logPath),error
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
}

static async Task<HttpResponseMessage> PostWakeAsync(int port,string origin,string pairing,string requestId)
{
    using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(4)};
    using var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{port}/v1/wake");
    request.Headers.TryAddWithoutValidation("Origin",origin);
    request.Headers.TryAddWithoutValidation("X-Sokna-Bridge-Pairing",pairing);
    request.Content=new StringContent(JsonSerializer.Serialize(new{type="print.wake",protocol_version=1,request_id=requestId,job_ids=new[]{101},expires_at=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")}),Encoding.UTF8,"application/json");
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

static async Task<bool> CanConnectAsync(int port)
{
    using var client=new TcpClient();
    using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    try{await client.ConnectAsync(IPAddress.Loopback,port,cts.Token);return true;}catch{return false;}
}

static async Task<BridgeRuntimeSnapshot> WaitListeningAsync(BridgeRuntimeState runtime,int port,TimeSpan timeout)
{
    await WaitAsync(()=>runtime.Snapshot.Listening&&runtime.Snapshot.Port==port,timeout,$"listener {port}");
    return runtime.Snapshot;
}

static async Task<BridgeRuntimeSnapshot> WaitGenerationAsync(BridgeRuntimeState runtime,long prior,int port,TimeSpan timeout)
{
    await WaitAsync(()=>runtime.Snapshot.Generation>prior&&runtime.Snapshot.Listening&&runtime.Snapshot.Port==port,timeout,"bridge generation reload");
    return runtime.Snapshot;
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
    public BridgeRuntimeState Runtime{get;}=new();
    public PrintWakeSignal Wake{get;}=new();

    private BridgeEnvironment(string root)
    {
        _root=root;
        Paths=new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
    }

    public static BridgeEnvironment Create()=>new(Path.Combine(Path.GetTempPath(),$"sokna-bridge-runtime-{Guid.NewGuid():N}"));

    public void SaveOptions(int port,string origin,bool enabled)
        =>new AgentOptions{ServerBaseUrl=origin,RequireHttps=true,LocalBridgeEnabled=enabled,LocalBridgePort=port,LocalBridgeAllowedOrigin=origin}.Save(Paths.ConfigPath);

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
