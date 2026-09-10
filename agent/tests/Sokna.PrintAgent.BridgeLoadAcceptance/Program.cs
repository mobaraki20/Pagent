using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A36",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A36 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A36.log");
var resultPath=Path.Combine(resultsDirectory,"A36.result.json");
var assertions=new List<string>();
var failures=new List<string>();
var evidence=new Dictionary<string,object?>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    Check(OperatingSystem.IsWindows(),"A36 executes on Windows HTTP.sys/HttpListener semantics");
    using var env=BridgeEnvironment.Create();
    var port=FreePort();
    const string origin="https://load-cashier.example";
    env.SaveOptions(port,origin,true);
    var pairing=LocalBridgeService.GetOrCreatePairingId(env.Paths);
    await env.StartAsync();
    await WaitAsync(()=>env.Runtime.Snapshot.Listening&&env.Runtime.Snapshot.Port==port,TimeSpan.FromSeconds(6),"bridge listener");

    Log("stage=unknown-length");
    var chunked=await SendChunkedAsync(port,origin,pairing,TimeSpan.FromSeconds(3));
    evidence["unknown_length_status"]=chunked;
    Check(chunked==411,"unknown/chunked request length is rejected with 411");

    Log("stage=slow-body");
    var slow=await ObserveIncompleteBodyAsync(port,origin,pairing,TimeSpan.FromSeconds(8));
    evidence["slow_body_termination_kind"]=slow.Kind;
    evidence["slow_body_http_status"]=slow.StatusCode;
    evidence["slow_body_elapsed_ms"]=slow.ElapsedMilliseconds;
    Log($"slow-body termination={slow.Kind}; status={slow.StatusCode?.ToString()??"none"}; elapsed_ms={slow.ElapsedMilliseconds}");
    Check(slow.Kind!="client_watchdog_timeout","incomplete body ownership terminates before client watchdog deadline");
    Check(slow.Kind=="server_closed"||slow.StatusCode==408,"incomplete body terminates by controlled 408 or server-side HTTP.sys close");
    Check(slow.ElapsedMilliseconds<8000,"slow body lifetime is bounded below the 8s acceptance watchdog");

    Log("stage=connection-saturation");
    var held=await Task.WhenAll(Enumerable.Range(0,24).Select(i=>OpenHeldBodyAsync(port,origin,pairing,i,TimeSpan.FromSeconds(2))));
    try
    {
        // All 24 clients have delivered complete headers plus a partial entity body. Give HttpListener
        // a short scheduling window, but stay well inside the 5s EntityBody timeout.
        await Task.Delay(250);
        var overflow=await SendWakeAsync(port,origin,pairing,"a36-overflow-0001",TimeSpan.FromSeconds(3));
        evidence["overflow_status"]=(int?)overflow;
        Log($"overflow status={(int?)overflow}");
        Check(overflow==(HttpStatusCode)503,"25th request receives explicit 503 while all bridge handler slots are occupied");
    }
    finally
    {
        foreach(var client in held)client.Dispose();
    }

    Log("stage=recovery");
    await Task.Delay(500);
    var recovery=await SendWakeAsync(port,origin,pairing,"a36-recovery-0001",TimeSpan.FromSeconds(4));
    evidence["recovery_status"]=(int?)recovery;
    Check(recovery==HttpStatusCode.OK,"bridge accepts a valid wake after load pressure is released");
    Check(env.Runtime.Snapshot.Listening,"listener remains active after bounded slow-body/storm pressure");

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }

    Log($"PASS A36; assertions={assertions.Count}");
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
        case_id="A36",status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real HTTP.sys/HttpListener + TcpClient",
        exit_code=exitCode,
        test_name="A36 bounded Local Bridge body lifetime, connection saturation and recovery",
        test_path="tests/Sokna.PrintAgent.BridgeLoadAcceptance/Program.cs",
        command="dotnet run --project tests/Sokna.PrintAgent.BridgeLoadAcceptance/Sokna.PrintAgent.BridgeLoadAcceptance.csproj -c Release --no-build -- --case A36 --results <dir>",
        assertions,failed_assertions=failures,evidence,raw_log=Path.GetFileName(logPath),error
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
}

static async Task<int> SendChunkedAsync(int port,string origin,string pairing,TimeSpan timeout)
{
    using var client=new TcpClient();
    using var cts=new CancellationTokenSource(timeout);
    await client.ConnectAsync(IPAddress.Loopback,port,cts.Token);
    var stream=client.GetStream();
    var request=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n0\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request),cts.Token);
    return await ReadHttpStatusAsync(stream,cts.Token);
}

static async Task<BodyTermination> ObserveIncompleteBodyAsync(int port,string origin,string pairing,TimeSpan watchdog)
{
    using var client=new TcpClient();
    using var cts=new CancellationTokenSource(watchdog);
    var sw=Stopwatch.StartNew();
    try
    {
        await client.ConnectAsync(IPAddress.Loopback,port,cts.Token);
        var stream=client.GetStream();
        var request=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request),cts.Token);
        var buffer=new byte[1024];
        var count=await stream.ReadAsync(buffer,cts.Token);
        sw.Stop();
        if(count==0)return new("server_closed",null,sw.ElapsedMilliseconds);
        var status=ParseStatus(buffer,count);
        return new("http_status",status,sw.ElapsedMilliseconds);
    }
    catch(OperationCanceledException) when(cts.IsCancellationRequested)
    {
        sw.Stop();
        return new("client_watchdog_timeout",null,sw.ElapsedMilliseconds);
    }
    catch(IOException)
    {
        sw.Stop();
        return new("server_closed",null,sw.ElapsedMilliseconds);
    }
    catch(SocketException)
    {
        sw.Stop();
        return new("server_closed",null,sw.ElapsedMilliseconds);
    }
}

static async Task<TcpClient> OpenHeldBodyAsync(int port,string origin,string pairing,int index,TimeSpan timeout)
{
    var client=new TcpClient();
    try
    {
        using var cts=new CancellationTokenSource(timeout);
        await client.ConnectAsync(IPAddress.Loopback,port,cts.Token);
        var stream=client.GetStream();
        var request=$"POST /v1/wake HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: {origin}\r\nX-Sokna-Bridge-Pairing: {pairing}\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: keep-alive\r\n\r\n{{\"held\":{index},";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request),cts.Token);
        return client;
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

static async Task<HttpStatusCode?> SendWakeAsync(int port,string origin,string pairing,string requestId,TimeSpan timeout)
{
    using var client=new HttpClient{Timeout=timeout};
    using var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{port}/v1/wake");
    request.Headers.TryAddWithoutValidation("Origin",origin);
    request.Headers.TryAddWithoutValidation("X-Sokna-Bridge-Pairing",pairing);
    request.Content=new StringContent(WakeJson(requestId),Encoding.UTF8,"application/json");
    try
    {
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead);
        return response.StatusCode;
    }
    catch(TaskCanceledException){return null;}
    catch(HttpRequestException){return null;}
}

static string WakeJson(string requestId)=>JsonSerializer.Serialize(new
{
    type="print.wake",protocol_version=1,request_id=requestId,job_ids=new[]{101},expires_at=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")
});

static async Task<int> ReadHttpStatusAsync(NetworkStream stream,CancellationToken ct)
{
    var buffer=new byte[1024];
    var count=await stream.ReadAsync(buffer,ct);
    if(count<=0)throw new IOException("Bridge closed before returning the expected HTTP status.");
    return ParseStatus(buffer,count);
}

static int ParseStatus(byte[] buffer,int count)
{
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

sealed record BodyTermination(string Kind,int? StatusCode,long ElapsedMilliseconds);

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

    public static BridgeEnvironment Create()=>new(Path.Combine(Path.GetTempPath(),$"sokna-bridge-load-{Guid.NewGuid():N}"));

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
