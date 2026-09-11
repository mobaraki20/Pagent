using System.Text;
using System.Text.Json;
namespace Sokna.PrintAgent.Core;
public sealed record AgentOptions
{
    public string ServerBaseUrl {get;init;}="";
    public string AgentName {get;init;}=Environment.MachineName;
    public int ActivePollMilliseconds {get;init;}=1000;
    public int IdlePollMilliseconds {get;init;}=4000;
    public int HeartbeatSeconds {get;init;}=15;
    public int ClaimBatchSize {get;init;}=3;
    public int WorkerTimeoutSeconds {get;init;}=25;
    public int WorkerExitProofTimeoutMilliseconds {get;init;}=3000;
    public int WorkerShutdownExitProofTimeoutMilliseconds {get;init;}=5000;
    public bool RequireHttps {get;init;}=true;
    public bool LocalBridgeEnabled {get;init;}=true;
    public int LocalBridgePort {get;init;}=17653;
    public string LocalBridgeAllowedOrigin {get;init;}="";

    // End-to-end PDF routing is an explicit UAT/development capability. It is OFF by default so
    // a production destination can never silently route to a file merely because the Agent is installed.
    public bool PdfTestSinkEnabled {get;init;}=false;

    // Preview is deliberately a small, read-only side workload. These defaults implement the
    // documented R07 policy: one active renderer globally, at most one queued revision per
    // session, and a small bounded global pending set. None of these settings grant print rights.
    public int PreviewMaxPendingGlobal {get;init;}=4;
    public int PreviewTimeoutSeconds {get;init;}=10;
    public int PreviewExitProofTimeoutMilliseconds {get;init;}=2000;
    public int PreviewMaxPayloadBytes {get;init;}=240000;
    public int PreviewMaxTextCharacters {get;init;}=100000;
    public int PreviewMaxItems {get;init;}=500;
    public int PreviewMaxHeightPixels {get;init;}=24000;
    public long PreviewMaxPixelArea {get;init;}=24000000;
    public int PreviewMaxOutputBytes {get;init;}=2000000;
    public int PreviewRevisionTtlSeconds {get;init;}=600;

    public static AgentOptions Load(string path)=>JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(path,Encoding.UTF8),JsonOptions())??throw new InvalidDataException("config.json معتبر نیست.");

    public void Save(string path)
    {
        Validate();var dir=Path.GetDirectoryName(path)??throw new InvalidDataException("مسیر config معتبر نیست.");Directory.CreateDirectory(dir);
        var tmp=Path.Combine(dir,$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this,JsonOptions()));
        try
        {
            using(var fs=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {fs.Write(bytes);fs.Flush(true);}
            File.Move(tmp,path,true);
        }
        finally{try{if(File.Exists(tmp))File.Delete(tmp);}catch{}}
    }

    public void Validate()
    {
        if(!Uri.TryCreate(ServerBaseUrl,UriKind.Absolute,out var uri))throw new InvalidDataException("ServerBaseUrl معتبر نیست.");
        if(!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))throw new InvalidDataException("ServerBaseUrl نباید شامل credential، query یا fragment باشد.");
        if(RequireHttps&&uri.Scheme!="https"&&!uri.IsLoopback)throw new InvalidDataException("Production فقط HTTPS مجاز است.");
        if(uri.Scheme is not ("https" or "http"))throw new InvalidDataException("فقط HTTP/HTTPS برای ServerBaseUrl مجاز است.");
        if(ClaimBatchSize is <1 or >5)throw new InvalidDataException("ClaimBatchSize باید بین 1 و 5 باشد.");
        if(ActivePollMilliseconds is <500 or >10000)throw new InvalidDataException("ActivePollMilliseconds خارج از محدوده مجاز است.");
        if(IdlePollMilliseconds is <1500 or >30000)throw new InvalidDataException("IdlePollMilliseconds خارج از محدوده مجاز است.");
        if(HeartbeatSeconds is <10 or >60)throw new InvalidDataException("HeartbeatSeconds باید بین 10 و 60 باشد.");
        if(WorkerTimeoutSeconds is <10 or >120)throw new InvalidDataException("WorkerTimeoutSeconds باید بین 10 و 120 باشد.");
        if(WorkerExitProofTimeoutMilliseconds is <500 or >15000)throw new InvalidDataException("WorkerExitProofTimeoutMilliseconds باید بین 500 و 15000 باشد.");
        if(WorkerShutdownExitProofTimeoutMilliseconds is <500 or >20000)throw new InvalidDataException("WorkerShutdownExitProofTimeoutMilliseconds باید بین 500 و 20000 باشد.");
        if(LocalBridgePort is <1024 or >65535)throw new InvalidDataException("LocalBridgePort معتبر نیست.");
        if(LocalBridgeEnabled && !string.IsNullOrWhiteSpace(LocalBridgeAllowedOrigin))
        {
            if(!Uri.TryCreate(LocalBridgeAllowedOrigin,UriKind.Absolute,out var origin)||origin.Scheme is not ("https" or "http")||origin.AbsolutePath!="/"||!string.IsNullOrEmpty(origin.Query)||!string.IsNullOrEmpty(origin.Fragment))throw new InvalidDataException("LocalBridgeAllowedOrigin باید Origin دقیق سایت باشد.");
        }
        if(PreviewMaxPendingGlobal is <1 or >16)throw new InvalidDataException("PreviewMaxPendingGlobal باید بین 1 و 16 باشد.");
        if(PreviewTimeoutSeconds is <2 or >30)throw new InvalidDataException("PreviewTimeoutSeconds باید بین 2 و 30 باشد.");
        if(PreviewExitProofTimeoutMilliseconds is <500 or >10000)throw new InvalidDataException("PreviewExitProofTimeoutMilliseconds باید بین 500 و 10000 باشد.");
        if(PreviewMaxPayloadBytes is <16384 or >262144)throw new InvalidDataException("PreviewMaxPayloadBytes خارج از محدوده امن است.");
        if(PreviewMaxTextCharacters is <4096 or >200000)throw new InvalidDataException("PreviewMaxTextCharacters خارج از محدوده امن است.");
        if(PreviewMaxItems is <20 or >1000)throw new InvalidDataException("PreviewMaxItems خارج از محدوده امن است.");
        if(PreviewMaxHeightPixels is <2000 or >48000)throw new InvalidDataException("PreviewMaxHeightPixels خارج از محدوده امن است.");
        if(PreviewMaxPixelArea is <2000000 or >48000000)throw new InvalidDataException("PreviewMaxPixelArea خارج از محدوده امن است.");
        if(PreviewMaxOutputBytes is <262144 or >8000000)throw new InvalidDataException("PreviewMaxOutputBytes خارج از محدوده امن است.");
        if(PreviewRevisionTtlSeconds is <60 or >3600)throw new InvalidDataException("PreviewRevisionTtlSeconds باید بین 60 و 3600 باشد.");
    }
    public static JsonSerializerOptions JsonOptions()=>new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=true};
}
