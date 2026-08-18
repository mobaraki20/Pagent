using System.Text;
using System.Text.Json;
namespace Sokna.PrintAgent.Core;
public static class DurableFile
{
    public static async Task WriteTextAtomicAsync(string path,string content,CancellationToken ct=default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var fs=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            await using(var writer=new StreamWriter(fs,new UTF8Encoding(false),4096,leaveOpen:true))
            {
                await writer.WriteAsync(content.AsMemory(),ct);
                await writer.FlushAsync(ct);
                fs.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally
        {
            try{if(File.Exists(tmp))File.Delete(tmp);}catch{/* best-effort temp cleanup; destination durability is already decided */}
        }
    }
    public static Task WriteJsonAtomicAsync<T>(string path,T value,CancellationToken ct=default)=>WriteTextAtomicAsync(path,JsonSerializer.Serialize(value,AgentOptions.JsonOptions()),ct);
    public static async Task TouchAtomicAsync(string path,string value,CancellationToken ct=default)=>await WriteTextAtomicAsync(path,value,ct);
}
