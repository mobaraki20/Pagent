using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var parsed=ParseArgs(args);
if(!parsed.TryGetValue("case",out var caseId)||!string.Equals(caseId,"A33",StringComparison.OrdinalIgnoreCase)||
   !parsed.TryGetValue("results",out var resultsDirectory)||string.IsNullOrWhiteSpace(resultsDirectory))
{
    Console.Error.WriteLine("Usage: --case A33 --results <directory>");
    return 64;
}

Directory.CreateDirectory(resultsDirectory);
var started=DateTimeOffset.UtcNow;
var logPath=Path.Combine(resultsDirectory,"A33.log");
var resultPath=Path.Combine(resultsDirectory,"A33.result.json");
var assertions=new List<string>();
var failures=new List<string>();
var evidence=new List<object>();
void Check(bool value,string name){assertions.Add(name);if(!value)failures.Add(name);}
void Log(string text){Console.WriteLine(text);File.AppendAllText(logPath,$"{DateTimeOffset.UtcNow:O}\t{text}{Environment.NewLine}");}

try
{
    Check(OperatingSystem.IsWindows(),"A33 executes on Windows HttpListener semantics");
    using var env=BridgeEnvironment.Create();
    var pairingPath=LocalBridgeService.PairingPath(env.Paths);
    try{File.Delete(pairingPath);}catch{}

    var pairingTasks=Enumerable.Range(0,32).Select(_=>Task.Run(()=>LocalBridgeService.GetOrCreatePairingId(env.Paths))).ToArray();
    var values=await Task.WhenAll(pairingTasks);
    var winner=File.ReadAllText(pairingPath,Encoding.UTF8).Trim();
    evidence.Add(new{stage="pairing_race",callers=values.Length,distinct=values.Distinct(StringComparer.Ordinal).Count(),winner_length=winner.Length});
    Check(values.Distinct(StringComparer.Ordinal).Count()==1,"concurrent pairing initialization publishes exactly one credential");
    Check(values.All(x=>x==winner)&&winner.Length>=20,"every pairing caller observes the same durable credential");

    var port=FreePort();
    using var occupied=new TcpListener(IPAddress.Loopback,port);
    occupied.Start();
    env.SaveOptions(port,"https://bind-test.example",true);
    await env.StartAsync();
    await WaitAsync(()=>env.Runtime.Snapshot.ErrorCode=="bind_failed",TimeSpan.FromSeconds(7),"bind failure visibility");
    var snapshot=env.Runtime.Snapshot;
    evidence.Add(new{stage="occupied_port",runtime=snapshot});
    Check(!snapshot.Listening&&snapshot.Enabled&&snapshot.ErrorCode=="bind_failed","occupied port never produces a fake listening state");
    Check(!string.IsNullOrWhiteSpace(snapshot.UpdatedAt),"bind failure has a fresh runtime state timestamp");

    if(failures.Count>0)
    {
        Log("FAIL "+string.Join(",",failures));
        await WriteResult("FAIL",1,string.Join(",",failures));
        return 1;
    }
    Log($"PASS A33; assertions={assertions.Count}");
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
        case_id="A33",status,source_sha=ResolveSourceSha(),run_started_at=started.ToString("O"),run_finished_at=DateTimeOffset.UtcNow.ToString("O"),
        environment=$"{Environment.OSVersion}; .NET {Environment.Version}; real HttpListener/TcpListener",
        exit_code=exitCode,
        test_name="A33 atomic bridge pairing initialization and bind-failure truth",
        test_path="tests/Sokna.PrintAgent.BridgeAcceptance/Program.cs",
        command="dotnet run --project tests/Sokna.PrintAgent.BridgeAcceptance/Sokna.PrintAgent.BridgeAcceptance.csproj -c Release --no-build -- --case A33 --results <dir>",
        assertions,failed_assertions=failures,evidence,raw_log=Path.GetFileName(logPath),error
    };
    await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
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
    public BridgeRuntimeState Runtime{get;}=new();
    public PrintWakeSignal Wake{get;}=new();

    private BridgeEnvironment(string root)
    {
        _root=root;
        Paths=new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
        Paths.EnsureDirectories();
    }

    public static BridgeEnvironment Create()=>new(Path.Combine(Path.GetTempPath(),$"sokna-bridge-bind-{Guid.NewGuid():N}"));

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
