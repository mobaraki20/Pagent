using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Sokna.PrintAgent.Setup;

internal static class Program
{
    private const string ResourceName = "Sokna.PrintAgent.Payload.zip";
    private static readonly string[] SensitiveMarkers = ["authorization", "bearer", "token", "secret", "hmac"];

    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));
        var skipStart = args.Any(a => string.Equals(a, "/skip-start", StringComparison.OrdinalIgnoreCase));
        var referenceId = Guid.NewGuid().ToString("N");
        var stage = "setup_bootstrap_start";
        var tempRoot = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSetup", referenceId);
        string stdout = "";
        string stderr = "";
        int? childExitCode = null;

        try
        {
            WriteDiagnostic(referenceId, stage, null, null, null, null, null);

            stage = "embedded_payload_extraction";
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

            stage = "payload_manifest_presence";
            var manifest = Path.Combine(tempRoot, "PAYLOAD_MANIFEST.json");
            var installer = Path.Combine(tempRoot, "Install-SoknaPrintAgent.ps1");
            if (!File.Exists(manifest) || !File.Exists(installer))
                throw new InvalidDataException("Installer package is incomplete.");

            stage = "powershell_installer_start";
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
                WorkingDirectory = tempRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Windows PowerShell installer.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            childExitCode = process.ExitCode;

            stage = "powershell_installer_exit";
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Installer returned exit code {process.ExitCode}.");

            stage = "completed";
            WriteDiagnostic(referenceId, stage, 0, childExitCode, null, null, null);
            if (!quiet) MessageBox(IntPtr.Zero, "Sokna Print Agent 6 نصب/به‌روزرسانی شد.", "Sokna Print Agent", 0x40);
            return 0;
        }
        catch (Exception ex)
        {
            var safeMessage = Sanitize(ex.Message, 900);
            var logPath = WriteDiagnostic(referenceId, stage, 1, childExitCode, stdout, stderr, ex.GetType().FullName, safeMessage);
            var visible = $"Installation failed. stage={stage}; ref={referenceId}; child_exit={(childExitCode?.ToString() ?? "n/a")}; log={logPath}";
            if (!quiet) MessageBox(IntPtr.Zero, visible, "خطای نصب Sokna Print Agent", 0x10);
            try
            {
                Console.Error.WriteLine(visible);
                var safeStderr = Sanitize(stderr, 1800);
                if (!string.IsNullOrWhiteSpace(safeStderr)) Console.Error.WriteLine(safeStderr);
            }
            catch { }
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string WriteDiagnostic(string referenceId, string stage, int? exitCode, int? childExitCode,
        string? stdout, string? stderr, string? exceptionType, string? exceptionMessage = null)
    {
        var payload = new
        {
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            reference_id = referenceId,
            stage,
            exit_code = exitCode,
            child_process_exit_code = childExitCode,
            exception_type = exceptionType,
            exception_message = Sanitize(exceptionMessage, 1200),
            stdout = Sanitize(stdout, 4000),
            stderr = Sanitize(stderr, 4000)
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        var primary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sokna", "PrintAgentSetup", "logs");
        var fallback = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSetupLogs");
        foreach (var root in new[] { primary, fallback })
        {
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, $"setup-{referenceId}.json");
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }
            catch { }
        }
        return "diagnostic-log-unavailable";
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var safeLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !SensitiveMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        var safe = string.Join(Environment.NewLine, safeLines).Trim();
        return safe.Length <= maxLength ? safe : safe[..maxLength];
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
