using Microsoft.Win32;
namespace Sokna.PrintAgent.Core;
public sealed record AgentPaths(string ProgramDataRoot,string ConfigPath,string SecretPath,string DatabasePath,string LogsPath,string WorkPath,string HealthPath)
{
    private const string RegistryKey=@"SOFTWARE\Sokna\PrintAgent";
    public static AgentPaths Default()
    {
        var root=ResolveProgramDataRoot();
        return new(root,Path.Combine(root,"config.json"),Path.Combine(root,"secret.dat"),Path.Combine(root,"queue.db"),Path.Combine(root,"logs"),Path.Combine(root,"work"),Path.Combine(root,"health.json"));
    }
    private static string ResolveProgramDataRoot()
    {
        var env=Environment.GetEnvironmentVariable("SOKNA_PRINT_AGENT_DATA_ROOT",EnvironmentVariableTarget.Process);
        if(!string.IsNullOrWhiteSpace(env))return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env.Trim()));
        if(OperatingSystem.IsWindows())
        {
            try
            {
                using var key=Registry.LocalMachine.OpenSubKey(RegistryKey,false);
                var configured=key?.GetValue("DataRoot",null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if(!string.IsNullOrWhiteSpace(configured))return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            }
            catch(UnauthorizedAccessException){}
            catch(System.Security.SecurityException){}
            catch(IOException){}
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Sokna","PrintAgent");
    }
    public void EnsureDirectories(){Directory.CreateDirectory(ProgramDataRoot);Directory.CreateDirectory(LogsPath);Directory.CreateDirectory(WorkPath);}
}
