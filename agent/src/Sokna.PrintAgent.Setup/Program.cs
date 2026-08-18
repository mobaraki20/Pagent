using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sokna.PrintAgent.Setup;

internal static class Program
{
    private const string ResourceName = "Sokna.PrintAgent.Payload.zip";

    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));
        var skipStart = args.Any(a => string.Equals(a, "/skip-start", StringComparison.OrdinalIgnoreCase));
        var tempRoot = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSetup", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "payload.zip");
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException("Installer payload is missing."))
            using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            ZipFile.ExtractToDirectory(zipPath, tempRoot, overwriteFiles: true);
            File.Delete(zipPath);

            var manifest = Path.Combine(tempRoot, "PAYLOAD_MANIFEST.json");
            var installer = Path.Combine(tempRoot, "Install-SoknaPrintAgent.ps1");
            if (!File.Exists(manifest) || !File.Exists(installer))
                throw new InvalidDataException("Installer package is incomplete.");

            // The PowerShell installer performs its own size/SHA-256 verification before touching live files.
            var psArgs = new StringBuilder();
            psArgs.Append("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ")
                  .Append(Quote(installer));
            if (skipStart) psArgs.Append(" -SkipStart");

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = psArgs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = quiet,
                WorkingDirectory = tempRoot
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Windows PowerShell installer.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Installer returned exit code {process.ExitCode}.");
            if (!quiet) MessageBox(IntPtr.Zero, "Sokna Print Agent 6 نصب/به‌روزرسانی شد.", "Sokna Print Agent", 0x40);
            return 0;
        }
        catch (Exception ex)
        {
            var safe = ex.Message.Length > 700 ? ex.Message[..700] : ex.Message;
            if (!quiet) MessageBox(IntPtr.Zero, safe, "خطای نصب Sokna Print Agent", 0x10);
            try { Console.Error.WriteLine(safe); } catch { }
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
