using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Sokna.PrintAgent.Setup;

internal static class DotNetDesktopRuntimePrerequisite
{
    internal const int RequiredMajor = 10;
    internal const string StableDownloadUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const long MaxInstallerBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan RuntimeInstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static DotNetRuntimeStatus Inspect()
    {
        foreach (var root in CandidateRoots())
        {
            var desktop = BestStableVersion(root, "Microsoft.WindowsDesktop.App");
            var core = BestStableVersion(root, "Microsoft.NETCore.App");
            if (desktop is not null && core is not null)
                return new DotNetRuntimeStatus(true, root, desktop.ToString(), core.ToString());
        }
        return new DotNetRuntimeStatus(false, null, null, null);
    }

    internal static async Task<DotNetRuntimeEnsureResult> EnsureAsync(
        string tempRoot,
        Action<string, string>? report,
        CancellationToken ct = default)
    {
        var existing = Inspect();
        report?.Invoke("dotnet_runtime_check", existing.Available
            ? $".NET Desktop Runtime {existing.DesktopVersion} x64 موجود است."
            : ".NET 10 Desktop Runtime x64 یافت نشد.");
        if (existing.Available)
            return new DotNetRuntimeEnsureResult(existing, false, false);

        Directory.CreateDirectory(tempRoot);
        var installer = Path.Combine(tempRoot, "windowsdesktop-runtime-win-x64.exe");
        try
        {
            report?.Invoke("dotnet_runtime_download", "در حال دریافت .NET 10 Desktop Runtime x64 از Microsoft…");
            await DownloadInstallerAsync(installer, ct);
            VerifyMicrosoftAuthenticodeSignature(installer);
            report?.Invoke("dotnet_runtime_install", "امضای Microsoft معتبر است؛ Runtime به‌صورت silent نصب می‌شود.");

            var psi = new ProcessStartInfo
            {
                FileName = installer,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/install");
            psi.ArgumentList.Add("/quiet");
            psi.ArgumentList.Add("/norestart");
            using var process = Process.Start(psi) ?? throw new InvalidOperationException(".NET Runtime installer process شروع نشد.");
            using var installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            installCts.CancelAfter(RuntimeInstallTimeout);
            try
            {
                await process.WaitForExitAsync(installCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($".NET Desktop Runtime installer پس از {RuntimeInstallTimeout.TotalMinutes:0} دقیقه پایان نیافت و متوقف شد.");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw;
            }

            var restartRequired = process.ExitCode == 3010;
            if (process.ExitCode is not (0 or 3010))
                throw new InvalidOperationException($".NET Desktop Runtime installer exit code={process.ExitCode}.");

            var installed = Inspect();
            if (!installed.Available)
            {
                var suffix = restartRequired ? " Windows restart may be required before retrying Setup." : "";
                throw new InvalidOperationException(".NET 10 Desktop Runtime installer پایان یافت اما Runtime x64 قابل استفاده Verify نشد." + suffix);
            }
            return new DotNetRuntimeEnsureResult(installed, true, restartRequired);
        }
        finally
        {
            try { if (File.Exists(installer)) File.Delete(installer); } catch { }
        }
    }

    internal static async Task VerifyDownloadOnlyAsync(string tempRoot, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempRoot);
        var installer = Path.Combine(tempRoot, "windowsdesktop-runtime-signature-check.exe");
        try
        {
            await DownloadInstallerAsync(installer, ct);
            VerifyMicrosoftAuthenticodeSignature(installer);
            Console.Error.WriteLine("SOKNA_RUNTIME_VERIFY=success source=microsoft_https authenticode=valid");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SOKNA_RUNTIME_VERIFY=failure type={ex.GetType().Name} message={Safe(ex.Message)}");
            throw;
        }
        finally
        {
            try { if (File.Exists(installer)) File.Delete(installer); } catch { }
        }
    }

    private static async Task DownloadInstallerAsync(string destination, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sokna-Print-Agent-Setup/6");
        using var response = await http.GetAsync(StableDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(".NET Runtime download did not resolve to HTTPS.");
        if (response.Content.Headers.ContentLength is long declared && (declared <= 0 || declared > MaxInstallerBytes))
            throw new InvalidDataException(".NET Runtime download size خارج از محدوده امن است.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            total = checked(total + read);
            if (total > MaxInstallerBytes) throw new InvalidDataException(".NET Runtime download از سقف اندازه امن عبور کرد.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await target.FlushAsync(ct);
        target.Flush(true);
        if (total < 1024 * 1024) throw new InvalidDataException(".NET Runtime download unexpectedly small است.");
        if (response.Content.Headers.ContentLength is long expected && total != expected)
            throw new EndOfStreamException($".NET Runtime download ناقص است: expected={expected} actual={total}.");
    }

    private static void VerifyMicrosoftAuthenticodeSignature(string installerPath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = installerPath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE; HTTPS + signer pinning remain independently enforced.
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfoPointer,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProviderFlags = 0,
                UiContext = 0
            };

            var trustStatus = WinVerifyTrust(new IntPtr(-1), WinTrustActionGenericVerifyV2, ref trustData);
            if (trustStatus != 0)
                throw new InvalidDataException($"Downloaded .NET Runtime Authenticode trust validation failed: 0x{trustStatus:X8}.");
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }

#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no modern managed replacement; trust itself is validated by WinVerifyTrust above.
        using var signer = X509Certificate.CreateFromSignedFile(installerPath);
#pragma warning restore SYSLIB0057
        var subject = signer.Subject ?? string.Empty;
        var microsoftOrganization = subject
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, "O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase));
        if (!microsoftOrganization)
            throw new InvalidDataException("Downloaded .NET Runtime signer is not Microsoft Corporation.");
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in RegisteredRoots().Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim())); }
            catch { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    private static IEnumerable<string> RegisteredRoots()
    {
        RegistryKey? baseKey = null;
        RegistryKey? key = null;
        RegistryKey? sharedHost = null;
        try
        {
            baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64", false);
            if (key?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string location)
                yield return location;
            sharedHost = key?.OpenSubKey("sharedhost", false);
            if (sharedHost?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string hostPath && !string.IsNullOrWhiteSpace(hostPath))
            {
                var root = File.Exists(hostPath) ? Path.GetDirectoryName(hostPath) : hostPath;
                if (!string.IsNullOrWhiteSpace(root)) yield return root;
            }
        }
        finally
        {
            sharedHost?.Dispose();
            key?.Dispose();
            baseKey?.Dispose();
        }
    }

    private static Version? BestStableVersion(string root, string framework)
    {
        try
        {
            var path = Path.Combine(root, "shared", framework);
            if (!Directory.Exists(path)) return null;
            return Directory.EnumerateDirectories(path)
                .Select(Path.GetFileName)
                .Select(name => Version.TryParse(name, out var version) ? version : null)
                .Where(version => version is not null && version.Major == RequiredMajor)
                .OrderByDescending(version => version)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..500];
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}

internal sealed record DotNetRuntimeStatus(bool Available, string? Root, string? DesktopVersion, string? CoreVersion);
internal sealed record DotNetRuntimeEnsureResult(DotNetRuntimeStatus Status, bool InstalledBySetup, bool RestartRequired);
