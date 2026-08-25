namespace Sokna.PrintAgent.Core;
public sealed class AgentLog
{
    private readonly string _dir;private readonly object _gate=new();
    public AgentLog(string directory){_dir=directory;Directory.CreateDirectory(_dir);}
    public void Info(string code,string message)=>Write("INFO",code,message);
    public void Error(string code,Exception e)=>Write("ERROR",code,$"{e.GetType().Name}: {e.Message}");
    public void Warn(string code,string message)=>Write("WARN",code,message);
    private void Write(string level,string code,string message)
    {
        var safe=SafeLogText.Sanitize(message);var line=$"{DateTimeOffset.UtcNow:O}\t{level}\t{SafeLogText.Sanitize(code,120)}\t{safe}{Environment.NewLine}";
        lock(_gate)File.AppendAllText(Path.Combine(_dir,$"agent-{DateTime.UtcNow:yyyyMMdd}.log"),line);
    }
}
