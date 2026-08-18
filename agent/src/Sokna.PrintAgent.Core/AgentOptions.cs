using System.Text;
using System.Text.Json;
namespace Sokna.PrintAgent.Core;
public sealed record AgentOptions
{
    public string ServerBaseUrl {get;init;}="";
    public string AgentName {get;init;}=Environment.MachineName;
    public int ActivePollMilliseconds {get;init;}=1500;
    public int IdlePollMilliseconds {get;init;}=4000;
    public int HeartbeatSeconds {get;init;}=15;
    public int ClaimBatchSize {get;init;}=3;
    public int WorkerTimeoutSeconds {get;init;}=25;
    public bool RequireHttps {get;init;}=true;

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
        if(ActivePollMilliseconds is <750 or >10000)throw new InvalidDataException("ActivePollMilliseconds خارج از محدوده مجاز است.");
        if(IdlePollMilliseconds is <1500 or >30000)throw new InvalidDataException("IdlePollMilliseconds خارج از محدوده مجاز است.");
        if(HeartbeatSeconds is <10 or >60)throw new InvalidDataException("HeartbeatSeconds باید بین 10 و 60 باشد.");
        if(WorkerTimeoutSeconds is <10 or >120)throw new InvalidDataException("WorkerTimeoutSeconds باید بین 10 و 120 باشد.");
    }
    public static JsonSerializerOptions JsonOptions()=>new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=true};
}
