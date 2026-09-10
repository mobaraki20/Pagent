using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public static class ServerScopeResolver
{
    public static string Resolve(string serverBaseUrl,string? serverInstanceId)
    {
        if(!string.IsNullOrWhiteSpace(serverInstanceId))
            return "instance:"+CryptoUtil.Sha256Hex(serverInstanceId.Trim())[..24];

        if(!Uri.TryCreate(serverBaseUrl,UriKind.Absolute,out var uri))
            throw new InvalidDataException("ServerBaseUrl برای ساخت server scope معتبر نیست.");
        var builder=new UriBuilder(uri)
        {
            Query=string.Empty,
            Fragment=string.Empty,
            Host=uri.IdnHost.ToLowerInvariant()
        };
        var path=builder.Path.TrimEnd('/');
        builder.Path=string.IsNullOrEmpty(path)?"/":path;
        var canonical=builder.Uri.GetComponents(UriComponents.SchemeAndServer|UriComponents.Path,UriFormat.UriEscaped).TrimEnd('/').ToLowerInvariant();
        return "url:"+CryptoUtil.Sha256Hex(canonical)[..24];
    }

    public static bool Supports(ProbeResponse probe,string capability)
        => probe.Capabilities?.Any(value=>string.Equals(value,capability,StringComparison.OrdinalIgnoreCase))==true;
}
