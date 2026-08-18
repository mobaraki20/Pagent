using System.Runtime.Versioning;
namespace Sokna.PrintAgent.Core;

public interface ILeaseTokenProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiLeaseTokenProtector : ILeaseTokenProtector
{
    public string Protect(string value) => SecretStore.ProtectText(value);
    public string Unprotect(string value) => SecretStore.UnprotectText(value);
}
