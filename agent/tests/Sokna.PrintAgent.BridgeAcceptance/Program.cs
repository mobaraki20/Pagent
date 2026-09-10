using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!new[]{"A32","A33","A34","A36"}.Contains(caseId,StringComparer.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A32|A33|A34|A36 --results <directory>");
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
    Check(OperatingSystem.IsWindows(),$"{caseId} executes on Windows listener semantics");
    switch(caseId.ToUpperInvariant())
    {
        case "A32": await RunReloadAsync(); break;
        case "A33": await RunBindAndPairingRaceAsync(); break;
        case "A34": await RunSecurityAsync(); break;
        case "A36": await RunBodyAndConnectionLimitsAsync(); break;
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
    var error=$"{e.GetType().Name}: {SafeLogText.Sanitize(e.Message,400)}";
    Log("FAIL "+error);
    await WriteResult("FAIL",1,error);
    return 1;
}

async Task RunReloadAsync()
{
    using var env=await BridgeEnvironment.CreateAsync();
    var port1=FreePort();
    var port2=FreePort();
    while(port2==port1)port2=FreePort();
    const string origin1="https://cashier-a.example";
    const string origin2="https://cashier-b.example";
    env.SaveOptions(port1,origin1,true);
    var pairing=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    var first=await WaitListeningAsync(env.Runtime,port1,TimeSpan.FromSeconds(6));
    Check(first.Listening&&first.Port==port1&&first.Origin==origin1,"initial bridge generation listens on configured port/origin");
    var ok=await PostWakeAsync(port1,origin1,pairing,"a32-first-0001");
    Check(ok.StatusCode==HttpStatusCode.OK,"initial generation accepts valid wake");

    env.SaveOptions(port2,origin2,true);
    var second=await WaitGenerationAsync(env.Runtime,first.Generation,port2,TimeSpan.FromSeconds(8));
    Check(second.Listening&&second.Generation>first.Generation&&second.Port==port2&&second.Origin==origin2,"config change replaces listener generation at runtime");
    Check(!await CanConnectAsync(port1),"old listener port stops accepting after runtime reload");
    var newPortOk=await PostWakeAsync(port2,origin2,pairing,"a32-second-0002");
    Check(newPortOk.StatusCode==HttpStatusCode.OK,"new listener generation accepts new origin");
    var oldOrigin=await PostWakeAsync(port2,origin1,pairing,"a32-old-origin");
    Check(oldOrigin.StatusCode==HttpStatusCode.Forbidden,"old origin is rejected after reload");

    var rotated=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    File.WriteAllText(LocalBridgeService.PairingPath(env.Paths),rotated,Encoding.UTF8);
    var third=await WaitGenerationAsync(env.Runtime,second.Generation,port2,TimeSpan.FromSeconds(8));
    Check(third.Generation>second.Generation&&third.Listening,"pairing rotation creates a fresh active listener generation");
    var oldPairing=await PostWakeAsync(port2,origin2,pairing,"a32-old-pair");
    var newPairing=await PostWakeAsync(port2,origin2,rotated,"a32-new-pair");
    Check(oldPairing.StatusCode==HttpStatusCode.Forbidden,"old pairing credential is rejected after rotation");
    Check(newPairing.StatusCode==HttpStatusCode.OK,"rotated pairing credential is accepted without service restart");

    env.SaveOptions(port2,origin2,false);
    await WaitAsync(()=>!env.Runtime.Snapshot.Enabled&&!env.Runtime.Snapshot.Listening,TimeSpan.FromSeconds(8),"bridge disable reload");
    Check(!env.Runtime.Snapshot.Listening,"runtime disable reports not-listening truth");
    Check(!await CanConnectAsync(port2),"disabled bridge no longer accepts new connections");
}

async Task RunBindAndPairingRaceAsync()
{
    using var env=await BridgeEnvironment.CreateAsync();
    var pairingPath=LocalBridgeService.PairingPath(env.Paths);
    try{File.Delete(pairingPath);}catch{}
    var pairingTasks=Enumerable.Range(0,32).Select(_=>Task.Run(()=>LocalBridgeService.GetOrCreatePairingId(env.Paths))).ToArray();
    var values=await Task.WhenAll(pairingTasks);
    var winner=File.ReadAllText(pairingPath,Encoding.UTF8).Trim();
    Check(values.Distinct(StringComparer.Ordinal).Count()==1,"concurrent pairing initialization publishes exactly one credential");
    Check(values.All(x=>x==winner)&&winner.Length>=20,"every pairing caller observes the same durable credential");

    var port=FreePort();
    using var occupied=new TcpListener(IPAddress.Loopback,port);
    occupied.Start();
    env.SaveOptions(port,"https://bind-test.example",true);
    await env.StartAsync();
    await WaitAsync(()=>env.Runtime.Snapshot.ErrorCode=="bind_failed",TimeSpan.FromSeconds(7),"bind failure visibility");
    var snapshot=env.Runtime.Snapshot;
    Check(!snapshot.Listening&&snapshot.ErrorCode=="bind_failed","occupied port never produces a fake listening state");
    Check(!string.IsNullOrWhiteSpace(snapshot.UpdatedAt),"bind failure has a fresh runtime state timestamp");
}

async Task RunSecurityAsync()
{
    using var env=await BridgeEnvironment.CreateAsync();
    var port=FreePort();
    const string origin="https://secure-cashier.example";
    env.SaveOptions(port,origin,true);
    var pairing=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    await WaitListeningAsync(env.Runtime,port,TimeSpan.FromSeconds(6));

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
    var first=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson(replayId));
    var second=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson(replayId));
    var secondBody=await second.Content.ReadAsStringAsync();
    Check(first.StatusCode==HttpStatusCode.OK&&second.StatusCode==HttpStatusCode.OK,"valid wake and exact replay receive controlled success");
    Check(secondBody.Contains("\"idempotent\":true",StringComparison.OrdinalIgnoreCase),"replayed wake is idempotently suppressed");

    var oversized=new string('x',9000);
    var tooLarge=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",oversized);
    Check(tooLarge.StatusCode==HttpStatusCode.RequestEntityTooLarge,"oversized bridge body is rejected before JSON processing");

    var invalidPreview="{\"type\":\"print.preview\",\"protocol_version\":1,\"revision\":\"bad\",\"payload_json\":7,\"paper_width_mm\":80,\"printable_width_mm\":72}";
    var preview=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/preview",invalidPreview);
    Check((int)preview.StatusCode==422,"invalid preview types are rejected before any Worker launch");
}

async Task RunBodyAndConnectionLimitsAsync()
{
    using var env=await BridgeEnvironment.CreateAsync();
    var port=FreePort();
    const string origin="https://load-cashier.example";
    env.SaveOptions(port,origin,true);
    var pairing=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    await WaitListeningAsync(env.Runtime,port,TimeSpan.FromSeconds(6));

    var chunkedStatus=await SendChunkedHeadersAsync(port,origin,pairing);
    Check(chunkedStatus==411,"unknown/chunked request length is rejected by explicit bridge policy");

    var slowStatus=await SendSlowBodyAsync(port,origin,pairing);
    Check(slowStatus==408,"slow/incomplete request body is terminated by the body deadline");

    var slowClients=new List<TcpClient>();
    try
    {
        for(var i=0;i<24;i++)slowClients.Add(await OpenHeldBodyAsync(port,origin,pairing,i));
        await Task.Delay(300);
        var overflow=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson("a36-overflow-0001"));
        Check((int)overflow.StatusCode==503,"connection storm is bounded with an explicit busy response");
    }
    finally
    {
        foreach(var client in slowClients)client.Dispose();
    }

    await Task.Delay(300);
    var recovery=await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson("a36-recovery-0001"));
    Check(recovery.StatusCode==HttpStatusCode.OK,"bridge remains responsive after slow-body/connection-storm pressure is released");
}

static async Task<HttpResponseMessage> PostWakeAsync(int port,string origin,string pairing,string requestId)
    =>await SendAsync(port,origin,pairing,HttpMethod.Post,"/v1/wake",WakeJson(requestId));

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
    var response=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead);
    return response;
}

static async Task<int> SendChunkedHeadersAsync(int port,string origin,string pairing)
{
    using var client=new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback,port);
    var stream=client.GetStream();
    var request=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n0\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    return await ReadStatusAsync(stream,TimeSpan.FromSeconds(4));
}

static async Task<int> SendSlowBodyAsync(int port,string origin,string pairing)
{
    using var client=new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback,port);
    var stream=client.GetStream();
    var headers=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
    return await ReadStatusAsync(stream,TimeSpan.FromSeconds(8));
}

static async Task<TcpClient> OpenHeldBodyAsync(int port,string origin,string pairing,int index)
{
    var client=new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback,port);
    var stream=client.GetStream();
    var headers=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: keep-alive\r\n\r\n{{\"n\":{index},";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
    return client;
}

static async Task<int> ReadStatusAsync(NetworkStream stream,TimeSpan timeout)
{
    using var cts=new CancellationTokenSource(timeout);
    var buffer=new byte[1024];
    var count=await stream.ReadAsync(buffer,cts.Token);
    if(count<=0)throw new IOException("Bridge closed without an HTTP response.");
    var text=Encoding.ASCII.GetString(buffer,0,count);
    var first=text.Split("\r\n",2)[0];
    var parts=first.Split(' ',StringSplitOptions.RemoveEmptyEntries);
    if(parts.Length<2||!int.TryParse(parts[1],out var status))throw new InvalidDataException("Invalid HTTP status line: "+first);
    return status;
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

async Task WriteResult(string status,int exitCode,string? error)
{
    var payload=new
    {
        case_id=caseId.ToUpperInvariant(),status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real HttpListener/TcpClient",
        exit_code=exitCode,
        test_name=$"{caseId} local bridge production listener acceptance",
        test_path="tests/Sokna.PrintAgent.BridgeAcceptance/Program.cs",
        command=$"dotnet run --project tests/Sokna.PrintAgent.BridgeAcceptance/Sokna.PrintAgent.BridgeAcceptance.csproj -c Release --no-build -- --case {caseId} --results <dir>",
        assertions,failed_assertions=failures,raw_log=Path.GetFileName(logPath),error
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

    public static Task<BridgeEnvironment> CreateAsync()=>Task.FromResult(new BridgeEnvironment(Path.Combine(Path.GetTempPath(),$"sokna-bridge-{Guid.NewGuid():N}")));

    public void SaveOptions(int port,string origin,bool enabled)
    {
        new AgentOptions
        {
            ServerBaseUrl=origin,
            RequireHttps=true,
            LocalBridgeEnabled=enabled,
            LocalBridgePort=port,
            LocalBridgeAllowedOrigin=origin
        }.Save(Paths.ConfigPath);
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
