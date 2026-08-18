using System.Security.Cryptography;
using System.Text;
namespace Sokna.PrintAgent.Core;
public static class CryptoUtil
{
    public static string Sha256Hex(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string NewRequestId()=>Guid.NewGuid().ToString("N");
    public static string NewLocalReceiptId()=>"r-"+Guid.NewGuid().ToString("N");
}
