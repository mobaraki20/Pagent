using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Sokna.PrintAgent.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));
        var skipStart = args.Any(a => string.Equals(a, "/skip-start", StringComparison.OrdinalIgnoreCase));

        ShortcutPreferences shortcuts;
        try
        {
            shortcuts = InstallerEngine.ResolveShortcutPreferences(args);
        }
        catch (Exception e)
        {
            if (!quiet)
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show(e.Message, "Sokna Print Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 2;
        }

        if (quiet)
            return InstallerEngine.RunQuietAsync(skipStart, shortcuts).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        using var form = new SetupForm(skipStart, shortcuts);
        Application.Run(form);
        return form.ExitCode;
    }
}

internal sealed class SetupForm : Form
{
    private readonly bool _skipStart;
    private readonly string? _installedVersion = InstallerEngine.GetInstalledVersion();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _stage = new();
    private readonly Label _detail = new();
    private readonly ProgressBar _progress = new();
    private readonly ListBox _steps = new();
    private readonly Button _primary = new();
    private readonly Button _secondary = new();
    private readonly Button _openAgent = new();
    private readonly Button _openLog = new();
    private readonly TextBox _technical = new();
    private readonly CheckBox _showDetails = new();
    private readonly CheckBox _startMenuShortcut = new();
    private readonly CheckBox _desktopShortcut = new();
    private readonly FlowLayoutPanel _shortcutOptions = new();
    private string? _diagnosticPath;

    public int ExitCode { get; private set; } = 1;

    public SetupForm(bool skipStart, ShortcutPreferences shortcuts)
    {
        _skipStart = skipStart;
        Text = "Sokna Print Agent";
        Width = 720;
        Height = 680;
        MinimumSize = new Size(720, 680);
        MaximumSize = new Size(720, 790);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);

        BuildUi();
        _startMenuShortcut.Checked = shortcuts.StartMenu;
        _desktopShortcut.Checked = shortcuts.Desktop;
        ShowWelcome();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(28, 24, 28, 22),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _title.AutoSize = true;
        _title.Font = new Font("Segoe UI", 19F, FontStyle.Bold);
        _title.ForeColor = Color.FromArgb(15, 23, 42);
        _title.Margin = new Padding(0, 0, 0, 6);

        _subtitle.AutoSize = true;
        _subtitle.MaximumSize = new Size(640, 0);
        _subtitle.ForeColor = Color.FromArgb(71, 85, 105);
        _subtitle.Margin = new Padding(0, 0, 0, 18);

        _stage.AutoSize = true;
        _stage.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _stage.ForeColor = Color.FromArgb(31, 75, 122);
        _stage.Margin = new Padding(0, 0, 0, 6);

        _detail.AutoSize = true;
        _detail.MaximumSize = new Size(640, 0);
        _detail.ForeColor = Color.FromArgb(100, 116, 139);
        _detail.Margin = new Padding(0, 0, 0, 10);

        _shortcutOptions.Dock = DockStyle.Top;
        _shortcutOptions.AutoSize = true;
        _shortcutOptions.FlowDirection = FlowDirection.RightToLeft;
        _shortcutOptions.WrapContents = false;
        _shortcutOptions.Padding = new Padding(0, 3, 0, 9);
        _shortcutOptions.Margin = new Padding(0);

        _startMenuShortcut.Text = "ساخت میانبر در Start Menu";
        _startMenuShortcut.AutoSize = true;
        _startMenuShortcut.Margin = new Padding(20, 0, 0, 0);
        _desktopShortcut.Text = "ساخت میانبر روی Desktop";
        _desktopShortcut.AutoSize = true;
        _desktopShortcut.Margin = new Padding(20, 0, 0, 0);
        _shortcutOptions.Controls.Add(_startMenuShortcut);
        _shortcutOptions.Controls.Add(_desktopShortcut);

        _progress.Dock = DockStyle.Top;
        _progress.Height = 18;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Margin = new Padding(0, 0, 0, 14);

        _steps.Dock = DockStyle.Fill;
        _steps.BorderStyle = BorderStyle.FixedSingle;
        _steps.BackColor = Color.White;
        _steps.HorizontalScrollbar = true;

        _showDetails.Text = "نمایش جزئیات فنی";
        _showDetails.AutoSize = true;
        _showDetails.Margin = new Padding(0, 10, 0, 6);
        _showDetails.CheckedChanged += (_, _) => ToggleDetails();

        _technical.Multiline = true;
        _technical.ReadOnly = true;
        _technical.ScrollBars = ScrollBars.Both;
        _technical.WordWrap = false;
        _technical.Height = 120;
        _technical.Dock = DockStyle.Top;
        _technical.Visible = false;
        _technical.Font = new Font("Consolas", 8.5F);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 14, 0, 0)
        };

        ConfigureButton(_primary, true);
        ConfigureButton(_secondary, false);
        ConfigureButton(_openAgent, false);
        ConfigureButton(_openLog, false);
        _primary.Click += Primary_Click;
        _secondary.Click += (_, _) => Close();
        _openAgent.Click += (_, _) => OpenAgent();
        _openLog.Click += (_, _) => OpenDiagnostic();
        buttons.Controls.Add(_primary);
        buttons.Controls.Add(_openAgent);
        buttons.Controls.Add(_openLog);
        buttons.Controls.Add(_secondary);

        root.Controls.Add(_title, 0, 0);
        root.Controls.Add(_subtitle, 0, 1);
        root.Controls.Add(_stage, 0, 2);
        root.Controls.Add(_detail, 0, 3);
        root.Controls.Add(_shortcutOptions, 0, 4);
        root.Controls.Add(_steps, 0, 5);
        root.Controls.Add(_showDetails, 0, 6);
        root.Controls.Add(_technical, 0, 7);
        root.Controls.Add(buttons, 0, 8);
        Controls.Add(root);
    }

    private static void ConfigureButton(Button button, bool primary)
    {
        button.AutoSize = false;
        button.Width = primary ? 150 : 130;
        button.Height = 40;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Margin = new Padding(8, 0, 0, 0);
        button.Cursor = Cursors.Hand;
        if (primary)
        {
            button.BackColor = Color.FromArgb(31, 75, 122);
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(31, 75, 122);
        }
        else
        {
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(51, 65, 85);
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        }
    }

    private void ShowWelcome()
    {
        var target = InstallerEngine.TargetVersion;
        var upgrade = !string.IsNullOrWhiteSpace(_installedVersion);
        _title.Text = upgrade ? "به‌روزرسانی Sokna Print Agent" : "نصب Sokna Print Agent";
        _subtitle.Text = upgrade
            ? $"نسخه فعلی {_installedVersion} به نسخه {target} به‌روزرسانی می‌شود. تنظیمات، Credential، SQLite، لاگ‌ها و وضعیت کاری ProgramData حفظ می‌شوند."
            : $"نسخه {target} نصب می‌شود. Windows Service به‌صورت Automatic Delayed Start تنظیم و پس از نصب سلامت آن بررسی می‌شود.";
        _stage.Text = "آماده شروع";
        _detail.Text = "میانبرهای موردنیاز را انتخاب کنید. برنامه پس از نصب در Windows Installed Apps نیز ثبت می‌شود.";
        _progress.Value = 0;
        _steps.Items.Clear();
        _steps.Items.Add("• اعتبارسنجی بسته با SHA-256");
        _steps.Items.Add("• نصب/به‌روزرسانی Service, Worker و Control Console");
        _steps.Items.Add("• حفظ ProgramData و Durable Queue");
        _steps.Items.Add("• بررسی Auto-start، Recovery و health.json");
        _steps.Items.Add("• ثبت استاندارد در Windows Installed Apps و اعمال میانبرهای انتخابی");
        _primary.Text = upgrade ? "به‌روزرسانی" : "نصب";
        _secondary.Text = "انصراف";
        _openAgent.Visible = false;
        _openLog.Visible = false;
        _showDetails.Visible = false;
        _shortcutOptions.Visible = true;
        _startMenuShortcut.Enabled = true;
        _desktopShortcut.Enabled = true;
    }

    private async void Primary_Click(object? sender, EventArgs e)
    {
        _primary.Enabled = false;
        _secondary.Enabled = false;
        _startMenuShortcut.Enabled = false;
        _desktopShortcut.Enabled = false;
        _steps.Items.Clear();
        _showDetails.Visible = true;
        _stage.Text = "در حال آماده‌سازی…";
        _detail.Text = "هیچ پنجره دیگری لازم نیست. مراحل واقعی نصب در همین صفحه نمایش داده می‌شوند.";

        var shortcuts = new ShortcutPreferences(_startMenuShortcut.Checked, _desktopShortcut.Checked);
        var result = await InstallerEngine.RunAsync(_skipStart, shortcuts, update =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => ApplyUpdate(update));
                return;
            }
            ApplyUpdate(update);
        });

        _diagnosticPath = result.DiagnosticPath;
        _technical.Text = result.TechnicalOutput;
        if (result.Success)
            ShowSuccess();
        else
            ShowFailure(result);
    }

    private void ApplyUpdate(InstallUpdate update)
    {
        _stage.Text = update.Title;
        _detail.Text = update.Detail;
        _progress.Value = Math.Clamp(update.Progress, 0, 100);
        if (!string.IsNullOrWhiteSpace(update.StepLine))
        {
            _steps.Items.Add(update.StepLine);
            _steps.TopIndex = Math.Max(0, _steps.Items.Count - 1);
        }
        if (!string.IsNullOrWhiteSpace(update.TechnicalLine))
            _technical.AppendText(update.TechnicalLine + Environment.NewLine);
    }

    private void ShowSuccess()
    {
        ExitCode = 0;
        _title.Text = string.IsNullOrWhiteSpace(_installedVersion) ? "Sokna Print Agent نصب شد" : "به‌روزرسانی با موفقیت انجام شد";
        _subtitle.Text = $"نسخه {InstallerEngine.TargetVersion} آماده است. Service و health validation با موفقیت عبور کردند و برنامه در Windows Installed Apps ثبت شده است.";
        _stage.Text = "آماده استفاده";
        _detail.Text = "برای تنظیم اتصال، تست API و مشاهده Diagnostics، Operations Console را باز کنید.";
        _progress.Value = 100;
        _shortcutOptions.Visible = false;
        _primary.Visible = false;
        _openAgent.Text = "باز کردن Print Agent";
        _openAgent.Visible = true;
        _secondary.Text = "پایان";
        _secondary.Enabled = true;
        _openLog.Visible = !string.IsNullOrWhiteSpace(_diagnosticPath);
        _openLog.Text = "گزارش نصب";
    }

    private void ShowFailure(InstallResult result)
    {
        ExitCode = 1;
        _title.Text = "نصب/به‌روزرسانی کامل نشد";
        _subtitle.Text = result.RollbackVerified
            ? "Setup خطا را ثبت کرد و بازگشت به وضعیت قبلی با موفقیت Verify شد. قبل از تلاش مجدد گزارش مرحله شکست را بررسی کنید."
            : "Setup در یکی از مراحل متوقف شد و بازگشت به وضعیت قبلی Verify نشده است. وضعیت Service و گزارش تشخیصی باید بررسی شود.";
        _stage.Text = "نیاز به بررسی";
        _detail.Text = $"مرحله: {result.FailedStage} · Reference: {result.ReferenceId}";
        _shortcutOptions.Visible = false;
        _primary.Visible = false;
        _secondary.Text = "بستن";
        _secondary.Enabled = true;
        _openLog.Text = "باز کردن گزارش";
        _openLog.Visible = !string.IsNullOrWhiteSpace(_diagnosticPath);
        _progress.Value = Math.Min(_progress.Value, 95);
    }

    private void ToggleDetails()
    {
        _technical.Visible = _showDetails.Checked;
        Height = _showDetails.Checked ? 770 : 680;
    }

    private void OpenAgent()
    {
        try
        {
            var root = InstallerEngine.GetInstallRoot();
            var exe = Path.Combine(root, "Control", "Sokna.PrintAgent.Control.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Control Console پیدا نشد.", exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Sokna Print Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenDiagnostic()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_diagnosticPath) && File.Exists(_diagnosticPath))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_diagnosticPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstallerEngine.SetupLogsDirectory}\"") { UseShellExecute = true });
        }
        catch { }
    }
}

internal static class InstallerEngine
{
    private const string ResourceName = "Sokna.PrintAgent.Payload.zip";
    private const string RegistryKey = @"SOFTWARE\Sokna\PrintAgent";
    private static readonly string[] SensitiveMarkers = ["authorization", "bearer", "token", "secret", "hmac"];

    public static string TargetVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+', 2)[0];
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string SetupLogsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sokna", "PrintAgentSetup", "logs");

    public static string? GetInstalledVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey, false);
            return key?.GetValue("Version") as string;
        }
        catch { return null; }
    }

    public static string GetInstallRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey, false);
            var value = key?.GetValue("InstallRoot") as string;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sokna", "PrintAgent");
    }

    public static ShortcutPreferences ResolveShortcutPreferences(string[] args)
    {
        var startOn = HasArg(args, "/start-menu-shortcut");
        var startOff = HasArg(args, "/no-start-menu-shortcut");
        var desktopOn = HasArg(args, "/desktop-shortcut");
        var desktopOff = HasArg(args, "/no-desktop-shortcut");
        if (startOn && startOff) throw new InvalidDataException("گزینه‌های Start Menu با هم تعارض دارند.");
        if (desktopOn && desktopOff) throw new InvalidDataException("گزینه‌های Desktop با هم تعارض دارند.");

        var current = GetShortcutPreferences();
        return new ShortcutPreferences(
            startOn ? true : startOff ? false : current.StartMenu,
            desktopOn ? true : desktopOff ? false : current.Desktop);
    }

    public static ShortcutPreferences GetShortcutPreferences()
    {
        var installed = !string.IsNullOrWhiteSpace(GetInstalledVersion());
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey, false);
            var startStored = key?.GetValue("CreateStartMenuShortcut");
            var desktopStored = key?.GetValue("CreateDesktopShortcut");
            var startPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Sokna Print Agent.lnk");
            var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Sokna Print Agent.lnk");
            var start = startStored is not null ? Convert.ToInt32(startStored) != 0 : installed ? File.Exists(startPath) : true;
            var desktop = desktopStored is not null ? Convert.ToInt32(desktopStored) != 0 : installed && File.Exists(desktopPath);
            return new ShortcutPreferences(start, desktop);
        }
        catch
        {
            return new ShortcutPreferences(true, installed);
        }
    }

    public static async Task<int> RunQuietAsync(bool skipStart, ShortcutPreferences shortcuts)
    {
        var result = await RunAsync(skipStart, shortcuts, _ => { });
        return result.Success ? 0 : 1;
    }

    public static async Task<InstallResult> RunAsync(bool skipStart, ShortcutPreferences shortcuts, Action<InstallUpdate> progress)
    {
        var referenceId = Guid.NewGuid().ToString("N");
        var stage = "setup_bootstrap_start";
        var tempRoot = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSetup", referenceId);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        int? childExitCode = null;

        try
        {
            progress(MapStage(stage));
            WriteDiagnostic(referenceId, stage, null, null, null, null, null);

            stage = "embedded_payload_extraction";
            progress(MapStage(stage));
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "payload.zip");
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException("Installer payload is missing."))
            using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
                output.Flush(true);
            }
            ZipFile.ExtractToDirectory(zipPath, tempRoot, overwriteFiles: true);
            File.Delete(zipPath);

            stage = "payload_manifest_presence";
            progress(MapStage(stage));
            var manifest = Path.Combine(tempRoot, "PAYLOAD_MANIFEST.json");
            var installer = Path.Combine(tempRoot, "Install-SoknaPrintAgent.ps1");
            if (!File.Exists(manifest) || !File.Exists(installer))
                throw new InvalidDataException("Installer package is incomplete.");

            stage = "powershell_installer_start";
            progress(MapStage(stage));
            var psArgs = new StringBuilder();
            psArgs.Append("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ")
                  .Append(Quote(installer));
            if (skipStart) psArgs.Append(" -SkipStart");
            psArgs.Append(shortcuts.StartMenu ? " -CreateStartMenuShortcut" : " -NoStartMenuShortcut");
            psArgs.Append(shortcuts.Desktop ? " -CreateDesktopShortcut" : " -NoDesktopShortcut");

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = psArgs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Windows PowerShell installer.");
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                var parsed = ParseStage(e.Data);
                if (parsed is not null)
                {
                    stage = parsed;
                    progress(MapStage(parsed) with { TechnicalLine = e.Data });
                }
                else progress(new InstallUpdate(stage, MapStage(stage).Title, MapStage(stage).Detail, MapStage(stage).Progress, null, e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderr) stderr.AppendLine(e.Data);
                progress(new InstallUpdate(stage, MapStage(stage).Title, MapStage(stage).Detail, MapStage(stage).Progress, null, Sanitize(e.Data, 900)));
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            childExitCode = process.ExitCode;

            stage = "powershell_installer_exit";
            progress(MapStage(stage));
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Installer returned exit code {process.ExitCode}.");

            stage = "completed";
            progress(MapStage(stage));
            var log = WriteDiagnostic(referenceId, stage, 0, childExitCode, stdout.ToString(), stderr.ToString(), null);
            return new InstallResult(true, 0, referenceId, stage, log, Sanitize(stdout + Environment.NewLine + stderr, 12000), false);
        }
        catch (Exception ex)
        {
            var log = WriteDiagnostic(referenceId, stage, 1, childExitCode, stdout.ToString(), stderr.ToString(), ex.GetType().FullName, ex.Message);
            var combined = Sanitize(stdout + Environment.NewLine + stderr + Environment.NewLine + ex, 16000);
            var rollbackVerified = combined.Contains("SOKNA_ROLLBACK_RESULT=success", StringComparison.Ordinal);
            return new InstallResult(false, 1, referenceId, stage, log, combined, rollbackVerified);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string? ParseStage(string line)
    {
        const string marker = "SOKNA_SETUP_STAGE=";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = line[(index + marker.Length)..];
        var end = rest.IndexOf(' ');
        return (end >= 0 ? rest[..end] : rest).Trim();
    }

    private static InstallUpdate MapStage(string stage) => stage switch
    {
        "setup_bootstrap_start" => new(stage, "شروع Setup", "آماده‌سازی محیط نصب.", 2, "شروع نصب", null),
        "embedded_payload_extraction" => new(stage, "آماده‌سازی بسته", "استخراج امن payload موقت.", 6, "استخراج بسته", null),
        "payload_manifest_presence" => new(stage, "بررسی ساختار بسته", "Manifest و Installer داخلی بررسی می‌شوند.", 10, "بررسی Manifest", null),
        "powershell_installer_start" => new(stage, "شروع موتور نصب", "موتور واحد Fresh Install / Upgrade اجرا شد.", 13, "شروع موتور نصب", null),
        "elevation_admin_check" => new(stage, "بررسی دسترسی", "Administrator access بررسی می‌شود.", 16, "بررسی دسترسی Administrator", null),
        "existing_install_lookup" => new(stage, "تشخیص نصب قبلی", "نسخه، مسیر و تنظیمات میانبر فعلی بررسی می‌شوند.", 19, "تشخیص Fresh/Upgrade", null),
        "install_paths_resolution" => new(stage, "بررسی مسیرها", "Program Files و ProgramData تعیین می‌شوند.", 22, "بررسی مسیرهای نصب", null),
        "embedded_payload_presence" => new(stage, "بررسی مؤلفه‌ها", "وجود Service، Worker و Control بررسی می‌شود.", 26, "بررسی مؤلفه‌ها", null),
        "payload_manifest_hash_validation" => new(stage, "اعتبارسنجی بسته", "SHA-256 تمام فایل‌های نصب قبل از تغییر سیستم بررسی می‌شود.", 34, "اعتبارسنجی SHA-256", null),
        "programdata_setup" => new(stage, "آماده‌سازی داده پایدار", "ProgramData و مسیرهای durable حفظ/آماده می‌شوند.", 39, "آماده‌سازی ProgramData", null),
        "programdata_acl" => new(stage, "اعمال امنیت داده", "ACL مسیر mutable بررسی و اعمال می‌شود.", 43, "اعمال ACL ProgramData", null),
        "existing_install_acl_repair" => new(stage, "ترمیم دسترسی نسخه فعلی", "ACL نصب قبلی پیش از Repair/Upgrade استاندارد می‌شود.", 46, "ترمیم ACL نسخه فعلی", null),
        "program_files_parent_preflight" => new(stage, "بررسی مسیر نصب", "امکان swap امن Program Files پیش از توقف سرویس آزموده می‌شود.", 47, "Preflight مسیر نصب", null),
        "payload_copy" => new(stage, "Stage نسخه جدید", "Binaryهای جدید خارج از مسیر live آماده می‌شوند.", 49, "Stage نسخه جدید", null),
        "program_files_layout_validation" => new(stage, "بررسی Layout", "Service/Worker/Control به‌صورت ایزوله بررسی می‌شوند.", 53, "بررسی Layout", null),
        "program_files_acl" => new(stage, "اعمال امنیت برنامه", "ACL مسیر Program Files اعمال می‌شود.", 56, "اعمال ACL Program Files", null),
        "previous_service_handling" => new(stage, "آماده‌سازی Service", "نسخه قبلی کنترل‌شده متوقف می‌شود.", 61, "توقف کنترل‌شده Service", null),
        "program_files_swap" => new(stage, "فعال‌سازی نسخه جدید", "Binaryها با امکان Rollback جایگزین می‌شوند.", 67, "جایگزینی نسخه", null),
        "configuration_registry" => new(stage, "ثبت تنظیمات نصب", "Version، مسیرها و انتخاب میانبرها ثبت می‌شوند.", 71, "ثبت Registry", null),
        "service_create_or_config" => new(stage, "پیکربندی Windows Service", "Service و Startup mode بررسی می‌شوند.", 76, "پیکربندی Service", null),
        "automatic_delayed_start_validation" => new(stage, "بررسی Auto-start", "Automatic Delayed Start اعتبارسنجی می‌شود.", 80, "بررسی Auto-start", null),
        "service_recovery" => new(stage, "بررسی Recovery", "Windows Service Recovery policy اعمال و Verify می‌شود.", 84, "بررسی Recovery", null),
        "bundled_font_validation" => new(stage, "بررسی فونت چاپ", "فونت‌های همراه Worker اعتبارسنجی می‌شوند.", 86, "بررسی فونت", null),
        "service_start" => new(stage, "راه‌اندازی Agent", "Windows Service نسخه جدید اجرا می‌شود.", 89, "راه‌اندازی Service", null),
        "health_json" => new(stage, "بررسی سلامت", "Setup منتظر health.json تازه از Service می‌ماند.", 93, "اعتبارسنجی Health", null),
        "component_path_validation" => new(stage, "بررسی نهایی مؤلفه‌ها", "تمام مسیرهای runtime دوباره Verify می‌شوند.", 95, "بررسی مؤلفه‌های نصب‌شده", null),
        "shortcut_registration" => new(stage, "اعمال میانبرها", "Start Menu و Desktop مطابق انتخاب شما ایجاد یا حذف می‌شوند.", 97, "اعمال Shortcut", null),
        "installed_apps_registration" => new(stage, "ثبت در Windows", "اطلاعات Uninstall و نسخه در Windows Installed Apps ثبت می‌شوند.", 98, "ثبت Installed Apps", null),
        "finalize" => new(stage, "تکمیل نصب", "نسخه پشتیبان موقت پس از Health موفق جمع‌آوری می‌شود.", 99, "تکمیل نصب", null),
        "powershell_installer_exit" => new(stage, "بررسی نتیجه", "نتیجه موتور نصب دریافت شد.", 99, null, null),
        "completed" => new(stage, "نصب کامل شد", "Service، Health و Windows registration عبور کردند.", 100, "پایان موفق", null),
        _ => new(stage, "در حال نصب…", stage, 50, stage, null)
    };

    private static string WriteDiagnostic(string referenceId, string stage, int? exitCode, int? childExitCode,
        string? stdout, string? stderr, string? exceptionType, string? exceptionMessage = null)
    {
        var payload = new
        {
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            reference_id = referenceId,
            target_version = TargetVersion,
            installed_version = GetInstalledVersion(),
            stage,
            exit_code = exitCode,
            child_process_exit_code = childExitCode,
            exception_type = exceptionType,
            exception_message = Sanitize(exceptionMessage, 1200),
            stdout = Sanitize(stdout, 6000),
            stderr = Sanitize(stderr, 6000)
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var fallback = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSetupLogs");
        foreach (var root in new[] { SetupLogsDirectory, fallback })
        {
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, $"setup-{referenceId}.json");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                return path;
            }
            catch { }
        }
        return "diagnostic-log-unavailable";
    }

    private static string Sanitize(object? value, int maxLength)
    {
        var text = Convert.ToString(value) ?? "";
        if (string.IsNullOrWhiteSpace(text)) return "";
        var safeLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !SensitiveMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        var safe = string.Join(Environment.NewLine, safeLines).Trim();
        return safe.Length <= maxLength ? safe : safe[..maxLength];
    }

    private static bool HasArg(string[] args, string value) => args.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

internal sealed record ShortcutPreferences(bool StartMenu, bool Desktop);
internal sealed record InstallUpdate(string Stage, string Title, string Detail, int Progress, string? StepLine, string? TechnicalLine);
internal sealed record InstallResult(bool Success, int ExitCode, string ReferenceId, string FailedStage, string DiagnosticPath, string TechnicalOutput, bool RollbackVerified);
