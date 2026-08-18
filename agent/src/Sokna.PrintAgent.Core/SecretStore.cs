using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
namespace Sokna.PrintAgent.Core;

[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Sokna.PrintAgent.v6.DPAPI");
    public static void Save(string path,string token)
    {
        if(string.IsNullOrWhiteSpace(token))throw new ArgumentException("Token خالی است.");
        var protectedBytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()),Entropy,DataProtectionScope.LocalMachine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var fs=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                fs.Write(protectedBytes);
                fs.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally
        {
            try{if(File.Exists(tmp))File.Delete(tmp);}catch{/* best-effort temp cleanup */}
        }
    }
    public static string Load(string path){var data=File.ReadAllBytes(path);return Encoding.UTF8.GetString(ProtectedData.Unprotect(data,Entropy,DataProtectionScope.LocalMachine));}
    public static string ProtectText(string value)=>Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Entropy,DataProtectionScope.LocalMachine));
    public static string UnprotectText(string value)=>Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),Entropy,DataProtectionScope.LocalMachine));
}
