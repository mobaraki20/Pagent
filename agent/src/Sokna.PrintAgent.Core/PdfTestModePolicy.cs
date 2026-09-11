namespace Sokna.PrintAgent.Core;

/// <summary>
/// Central opt-in policy for the end-to-end PDF test destination.
/// Missing/unreadable/invalid configuration always means disabled.
/// </summary>
public static class PdfTestModePolicy
{
    public static bool IsEnabled()
        =>IsEnabled(AgentPaths.Default().ConfigPath);

    public static bool IsEnabled(string configPath)
    {
        try
        {
            return File.Exists(configPath) && AgentOptions.Load(configPath).PdfTestSinkEnabled;
        }
        catch
        {
            return false;
        }
    }
}
