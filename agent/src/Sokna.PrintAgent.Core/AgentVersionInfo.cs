using System.Reflection;

namespace Sokna.PrintAgent.Core;

public static class AgentVersionInfo
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(AgentVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        var version = typeof(AgentVersionInfo).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
