using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Sokna.PrintAgent.Core;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;

namespace Sokna.PrintAgent.Control;

public partial class MainWindow : Window
{
    private const string ServiceName = "SoknaPrintAgent6";
    private const string RegistryKey = @"SOFTWARE\Sokna\PrintAgent";
    private readonly AgentPaths _paths = AgentPaths.Default();
    private readonly ObservableCollection<PrinterRow> _printers = [];
    private readonly ObservableCollection<TestRow> _tests = [];
    private readonly ObservableCollection<LogRow> _logs = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private LocalHealthSnapshot? _latestHealth;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        PrintersGrid.ItemsSource = _printers;
        TestsGrid.ItemsSource = _tests;
        LogsGrid.ItemsSource = _logs;
        MachineText.Text = Environment.MachineName;
        VersionText.Text = $"Agent {AgentVersionInfo.Current}";
        _applicationIcon = LoadApplicationIcon();
        _trayIcon = CreateTrayIcon(_applicationIcon);
        LoadExistingSettings();
        ShowPage("overview");
        RefreshEverything();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => RefreshOperationalSnapshot();
        _refreshTimer.Start();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon(System.Drawing.Icon icon)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("باز کردن پنل");
        open.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        var exit = new System.Windows.Forms.ToolStripMenuItem("خروج کامل");
        exit.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
        menu.Items.Add(open);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        var trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Sokna Print Agent",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return trayIcon;
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        var icon = string.IsNullOrWhiteSpace(executable)
            ? null
            : System.Drawing.Icon.ExtractAssociatedIcon(executable);
        return icon ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        base.OnClosed(e);
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "Sokna Print Agent",
            "برنامه در System Tray فعال است. برای باز کردن، روی آیکن دوبار کلیک کنید.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string page)
            ShowPage(page);
    }

    private void ShowPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PrintersPage.Visibility = page == "printers" ? Visibility.Visible : Visibility.Collapsed;
        TestsPage.Visibility = page == "tests" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { NavOverview, NavPrinters, NavTests, NavLogs, NavSettings })
            button.Tag = null;
        (page switch
        {
            "printers" => NavPrinters,
            "tests" => NavTests,
            "logs" => NavLogs,
            "settings" => NavSettings,
            _ => NavOverview
        }).Tag = "selected";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEverything();
    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => LoadLogs();

    private void RefreshEverything()
    {
        RefreshOperationalSnapshot();
        LoadLogs();
        LoadExistingSettings();
        DataPathText.Text = $"ProgramData: {_paths.ProgramDataRoot}";
        InstallPathText.Text = $"Program Files: {ResolveInstallRoot() ?? "نامشخص"}";
    }

    private void RefreshOperationalSnapshot()
    {
        var serviceStatus = GetServiceStatus(ServiceName);
        ServiceValue.Text = serviceStatus ?? "Not installed";

        _latestHealth = ReadHealth();
        _printers.Clear();
        if (_latestHealth is not null)
        {
            foreach (var printer in _latestHealth.Printers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                _printers.Add(new PrinterRow(printer));
        }

        var total = _latestHealth?.Printers.Count ?? 0;
        var ready = _latestHealth?.Printers.Count(IsReady) ?? 0;
        PrinterValue.Text = $"{ready} / {total}";
        PrinterDetail.Text = total == 0 ? "هیچ Queue برای Service دیده نمی‌شود" : "Queue قابل مشاهده زیر LocalSystem";
        BacklogValue.Text = (_latestHealth?.LocalBacklogCount ?? 0).ToString();
        BacklogDetail.Text = $"مبهم: {_latestHealth?.LocalUnknownCount ?? 0}";

        if (_latestHealth is null)
        {
            ApiValue.Text = "نامشخص";
            ApiDetail.Text = "health.json در دسترس نیست";
            DiagLastSuccess.Text = "—";
            DiagLastError.Text = "—";
            DiagLastAction.Text = "—";
            DiagLatency.Text = "—";
            SetHealthState("نیاز به بررسی", "Snapshot سلامت Agent در دسترس نیست. Service و مسیر ProgramData را بررسی کنید.", HealthTone.Warning, null);
            return;
        }

        ApiValue.Text = _latestHealth.ConsecutiveApiFailures > 0 ? "اختلال" : _latestHealth.LastApiSuccessAt is null ? "نامشخص" : "متصل";
        ApiDetail.Text = _latestHealth.LastApiLatencyMs is long ms ? $"آخرین latency: {ms} ms" : "latency ثبت نشده";
        DiagLastSuccess.Text = _latestHealth.LastApiSuccessAt ?? "—";
        DiagLastError.Text = _latestHealth.LastApiErrorCode ?? "—";
        DiagLastAction.Text = _latestHealth.LastSuccessfulAction ?? "—";
        DiagLatency.Text = _latestHealth.LastApiLatencyMs is long latency ? $"{latency} ms" : "—";

        var updated = DateTimeOffset.TryParse(_latestHealth.UpdatedAt, out var parsed) ? parsed : (DateTimeOffset?)null;
        var age = updated.HasValue ? DateTimeOffset.UtcNow - updated.Value : TimeSpan.MaxValue;
        var running = string.Equals(serviceStatus, "Running", StringComparison.OrdinalIgnoreCase);

        if (!running)
            SetHealthState("چاپ در دسترس نیست", "Windows Service فعال نیست. تا رفع این وضعیت Agent نمی‌تواند کار چاپ جدید انجام دهد.", HealthTone.Error, updated);
        else if (age > TimeSpan.FromSeconds(45))
            SetHealthState("نیاز به بررسی", "health.json تازه نیست؛ Service ممکن است Hang شده باشد یا امکان به‌روزرسانی Snapshot را نداشته باشد.", HealthTone.Warning, updated);
        else if (_latestHealth.LocalUnknownCount > 0)
            SetHealthState("نیاز به تصمیم انسانی", $"{_latestHealth.LocalUnknownCount} Attempt مبهم محلی وجود دارد. Auto-Reprint مجاز نیست.", HealthTone.Warning, updated);
        else if (_latestHealth.ConsecutiveApiFailures > 0)
            SetHealthState("ارتباط ناپایدار", $"{_latestHealth.ConsecutiveApiFailures} خطای API متوالی ثبت شده است. آخرین خطا: {_latestHealth.LastApiErrorCode ?? "نامشخص"}.", HealthTone.Warning, updated);
        else if (total == 0)
            SetHealthState("نیاز به تنظیم پرینتر", "Service سالم است اما هیچ Printer Queue زیر LocalSystem دیده نمی‌شود.", HealthTone.Warning, updated);
        else
            SetHealthState("آماده چاپ", "Windows Service فعال است و Snapshot جاری خطای بحرانی گزارش نمی‌کند.", HealthTone.Healthy, updated);
    }

    private void SetHealthState(string title, string description, HealthTone tone, DateTimeOffset? updated)
    {
        HealthTitle.Text = title;
        HealthDescription.Text = description;
        HealthUpdated.Text = updated.HasValue ? $"به‌روزرسانی: {updated.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "";
        HeaderStatusText.Text = title;

        var palette = tone switch
        {
            HealthTone.Healthy => (Bg: "#ECF8F1", Fg: "#17633B", Border: "#BEE5CE"),
            HealthTone.Error => (Bg: "#FFF1F2", Fg: "#A61B32", Border: "#F5C2C7"),
            _ => (Bg: "#FFF8E6", Fg: "#8A5A00", Border: "#F2D69A")
        };
        HealthHero.Background = Brush(palette.Bg);
        HealthHero.BorderBrush = Brush(palette.Border);
        HeaderStatusBadge.Background = Brush(palette.Bg);
        HeaderStatusText.Foreground = Brush(palette.Fg);
        HealthTitle.Foreground = Brush(palette.Fg);
    }

    private async void RunTests_Click(object sender, RoutedEventArgs e)
    {
        RunTestsButton.IsEnabled = false;
        _tests.Clear();
        try
        {
            await AddTestAsync("Windows Service", () => Task.FromResult(TestService()));
            await AddTestAsync("Service Account / Health", () => Task.FromResult(TestHealthSnapshot()));
            await AddTestAsync("Configuration", () => Task.FromResult(TestConfiguration()));
            await AddTestAsync("Credential", () => Task.FromResult(File.Exists(_paths.SecretPath) ? TestResult.Pass("Credential امن موجود است.") : TestResult.Fail("secret.dat وجود ندارد.")));
            await AddTestAsync("Windows Spooler", () => Task.FromResult(TestNamedService("Spooler", "Windows Spooler")));
            await AddTestAsync("Printer visibility", () => Task.FromResult(TestPrinters()));
            await AddTestAsync("Print API v4", TestApiConnectionAsync);
            _tests.Add(new TestRow("End-to-End Test Print", "MANUAL", "از پنل چاپ Sokna اجرا شود تا مسیر Server → API → Agent → Worker → Spooler واقعاً تست شود.", "—"));
        }
        finally
        {
            RunTestsButton.IsEnabled = true;
        }
    }

    private async Task AddTestAsync(string name, Func<Task<TestResult>> test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await test();
            sw.Stop();
            _tests.Add(new TestRow(name, result.Success ? "PASS" : "FAIL", result.Detail, $"{sw.ElapsedMilliseconds} ms"));
        }
        catch (Exception e)
        {
            sw.Stop();
            _tests.Add(new TestRow(name, "FAIL", Safe(e.Message), $"{sw.ElapsedMilliseconds} ms"));
        }
    }

    private TestResult TestService()
    {
        var status = GetServiceStatus(ServiceName);
        return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
            ? TestResult.Pass("SoknaPrintAgent6 در حال اجرا است.")
            : TestResult.Fail($"Service status: {status ?? "not installed"}");
    }

    private TestResult TestNamedService(string service, string label)
    {
        var status = GetServiceStatus(service);
        return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
            ? TestResult.Pass($"{label} فعال است.")
            : TestResult.Fail($"{label}: {status ?? "not installed"}");
    }

    private TestResult TestHealthSnapshot()
    {
        var health = ReadHealth();
        if (health is null) return TestResult.Fail("health.json خوانده نشد.");
        if (!health.ServiceAccountContext) return TestResult.Fail("Service زیر LocalSystem گزارش نشده است.");
        if (!DateTimeOffset.TryParse(health.UpdatedAt, out var updated)) return TestResult.Fail("updated_at نامعتبر است.");
        var age = DateTimeOffset.UtcNow - updated;
        return age <= TimeSpan.FromSeconds(45)
            ? TestResult.Pass($"Snapshot تازه است؛ age={Math.Round(age.TotalSeconds)}s")
            : TestResult.Fail($"Snapshot قدیمی است؛ age={Math.Round(age.TotalSeconds)}s");
    }

    private TestResult TestConfiguration()
    {
        if (!File.Exists(_paths.ConfigPath)) return TestResult.Fail("config.json وجود ندارد.");
        var options = AgentOptions.Load(_paths.ConfigPath);
        options.Validate();
        return TestResult.Pass($"Server: {options.ServerBaseUrl}");
    }

    private TestResult TestPrinters()
    {
        var health = ReadHealth();
        if (health is null) return TestResult.Fail("Health snapshot برای Printer discovery در دسترس نیست.");
        if (health.Printers.Count == 0) return TestResult.Fail("هیچ Queue برای LocalSystem دیده نمی‌شود.");
        var ready = health.Printers.Count(IsReady);
        return ready > 0
            ? TestResult.Pass($"{ready} از {health.Printers.Count} Queue آماده‌اند.")
            : TestResult.Fail($"{health.Printers.Count} Queue دیده می‌شود ولی هیچ‌کدام Ready نیستند.");
    }

    private async Task<TestResult> TestApiConnectionAsync()
    {
        var options = AgentOptions.Load(_paths.ConfigPath);
        options.Validate();
        var token = SecretStore.Load(_paths.SecretPath);
        if (string.IsNullOrWhiteSpace(token)) return TestResult.Fail("Credential خالی است.");
        using var http = new HttpClient();
        var api = new HttpPrintTransport(http, options.ServerBaseUrl, token);
        var sw = Stopwatch.StartNew();
        var probe = await api.ProbeAsync(CancellationToken.None);
        sw.Stop();
        if (!probe.Success || probe.ProtocolVersion != 4) return TestResult.Fail("Server پاسخ داد اما Print API v4 معتبر نیست.");
        return TestResult.Pass($"Protocol 4 · {probe.Destinations.Count} مقصد · {sw.ElapsedMilliseconds} ms · min {probe.MinimumAgentVersion} / recommended {probe.RecommendedAgentVersion}");
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        TestApiButton.IsEnabled = false;
        SettingsMessage.Text = "در حال تست اتصال…";
        try
        {
            var result = await TestApiConnectionAsync();
            SettingsMessage.Text = result.Success ? "اتصال API v4 برقرار است. " + result.Detail : "اتصال ناموفق: " + result.Detail;
            SettingsMessage.Foreground = result.Success ? Brush("#17633B") : Brush("#A61B32");
        }
        catch (Exception ex)
        {
            SettingsMessage.Text = "اتصال ناموفق: " + Safe(ex.Message);
            SettingsMessage.Foreground = Brush("#A61B32");
        }
        finally
        {
            TestApiButton.IsEnabled = true;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = File.Exists(_paths.ConfigPath) ? AgentOptions.Load(_paths.ConfigPath) : new AgentOptions();
            var updated = existing with { ServerBaseUrl = ServerUrlBox.Text.Trim(), AgentName = Environment.MachineName };
            updated.Validate();
            updated.Save(_paths.ConfigPath);
            if (!string.IsNullOrWhiteSpace(TokenBox.Password))
                SecretStore.Save(_paths.SecretPath, TokenBox.Password);
            TokenBox.Clear();
            CredentialState.Text = File.Exists(_paths.SecretPath) ? "Configured ✓" : "Missing";
            SettingsMessage.Text = "تنظیمات ذخیره شد. Service تغییر را خودکار بارگذاری می‌کند؛ Restart لازم نیست.";
            SettingsMessage.Foreground = Brush("#17633B");
            RefreshOperationalSnapshot();
        }
        catch (Exception ex)
        {
            SettingsMessage.Text = "ذخیره انجام نشد: " + Safe(ex.Message);
            SettingsMessage.Foreground = Brush("#A61B32");
        }
    }

    private void LoadExistingSettings()
    {
        try
        {
            if (File.Exists(_paths.ConfigPath))
                ServerUrlBox.Text = AgentOptions.Load(_paths.ConfigPath).ServerBaseUrl;
            CredentialState.Text = File.Exists(_paths.SecretPath) ? "Configured ✓" : "Missing";
        }
        catch (Exception e)
        {
            SettingsMessage.Text = "Config: " + Safe(e.Message);
        }
    }

    private void LoadLogs()
    {
        _logs.Clear();
        try
        {
            if (Directory.Exists(_paths.LogsPath))
            {
                var files = Directory.GetFiles(_paths.LogsPath, "agent-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(4)
                    .ToArray();
                foreach (var file in files)
                {
                    foreach (var line in ReadTail(file, 500))
                    {
                        var parts = line.Split('\t', 4);
                        if (parts.Length == 4)
                            _logs.Add(new LogRow(parts[0], parts[1], parts[2], parts[3]));
                    }
                }
            }

            var setupLogs = SetupLogsPath();
            if (Directory.Exists(setupLogs))
            {
                foreach (var file in Directory.GetFiles(setupLogs, "setup-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var root = doc.RootElement;
                        var time = GetJsonString(root, "timestamp_utc") ?? File.GetLastWriteTimeUtc(file).ToString("O");
                        var stage = GetJsonString(root, "stage") ?? "setup";
                        var exception = GetJsonString(root, "exception_message");
                        var exit = root.TryGetProperty("exit_code", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32().ToString() : null;
                        _logs.Add(new LogRow(time, string.IsNullOrWhiteSpace(exception) ? "SETUP" : "ERROR", stage, string.IsNullOrWhiteSpace(exception) ? $"Setup diagnostic · exit={exit ?? "n/a"}" : exception!));
                    }
                    catch { }
                }
            }

            var ordered = _logs.OrderByDescending(x => DateTimeOffset.TryParse(x.Time, out var t) ? t : DateTimeOffset.MinValue).Take(800).ToArray();
            _logs.Clear();
            foreach (var row in ordered) _logs.Add(row);
        }
        catch (Exception e)
        {
            _logs.Add(new LogRow(DateTimeOffset.Now.ToString("O"), "ERROR", "control", Safe(e.Message)));
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.LogsPath);
    private void OpenEventViewer_Click(object sender, RoutedEventArgs e) => StartShell("eventvwr.msc");

    private void OpenSoknaPrinting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = AgentOptions.Load(_paths.ConfigPath);
            var url = options.ServerBaseUrl.TrimEnd('/') + "/admin/printing.php";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Safe(ex.Message), "Sokna Print Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var target = Path.Combine(desktop, $"Sokna-PrintAgent-Support-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var temp = Path.Combine(Path.GetTempPath(), "SoknaPrintAgentSupport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var health = ReadHealth();
                var summary = new
                {
                    generated_at = DateTimeOffset.Now.ToString("O"),
                    machine = Environment.MachineName,
                    os = Environment.OSVersion.VersionString,
                    service_status = GetServiceStatus(ServiceName),
                    data_root = _paths.ProgramDataRoot,
                    install_root = ResolveInstallRoot(),
                    health,
                    database = File.Exists(_paths.DatabasePath) ? new FileInfo(_paths.DatabasePath) is var db ? new { exists = true, size = db.Length, modified_utc = db.LastWriteTimeUtc.ToString("O") } : null : null,
                    secret = new { configured = File.Exists(_paths.SecretPath) }
                };
                File.WriteAllText(Path.Combine(temp, "summary.json"), JsonSerializer.Serialize(summary, AgentOptions.JsonOptions()), Encoding.UTF8);

                if (File.Exists(_paths.ConfigPath))
                {
                    var options = AgentOptions.Load(_paths.ConfigPath);
                    File.WriteAllText(Path.Combine(temp, "config-redacted.json"), JsonSerializer.Serialize(options, AgentOptions.JsonOptions()), Encoding.UTF8);
                }
                CopyIfExists(_paths.HealthPath, Path.Combine(temp, "health.json"));

                var agentLogTarget = Path.Combine(temp, "agent-logs");
                Directory.CreateDirectory(agentLogTarget);
                if (Directory.Exists(_paths.LogsPath))
                    foreach (var file in Directory.GetFiles(_paths.LogsPath, "agent-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))
                        File.Copy(file, Path.Combine(agentLogTarget, Path.GetFileName(file)), true);

                var setupTarget = Path.Combine(temp, "setup-logs");
                Directory.CreateDirectory(setupTarget);
                if (Directory.Exists(SetupLogsPath()))
                    foreach (var file in Directory.GetFiles(SetupLogsPath(), "setup-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                        File.Copy(file, Path.Combine(setupTarget, Path.GetFileName(file)), true);

                var diagnostics = new StringBuilder();
                diagnostics.AppendLine("=== sc query ===");
                diagnostics.AppendLine(Capture("sc.exe", $"query {ServiceName}"));
                diagnostics.AppendLine("=== sc qc ===");
                diagnostics.AppendLine(Capture("sc.exe", $"qc {ServiceName}"));
                diagnostics.AppendLine("=== sc qfailure ===");
                diagnostics.AppendLine(Capture("sc.exe", $"qfailure {ServiceName}"));
                diagnostics.AppendLine("=== recent Agent Event Viewer ===");
                diagnostics.AppendLine(Capture("wevtutil.exe", "qe Application /q:\"*[System[Provider[@Name='Sokna.PrintAgent.Service']]]\" /f:text /c:200 /rd:true", 8000));
                File.WriteAllText(Path.Combine(temp, "windows-diagnostics.txt"), diagnostics.ToString(), Encoding.UTF8);

                ZipFile.CreateFromDirectory(temp, target, CompressionLevel.Optimal, false);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            MessageBox.Show($"بسته پشتیبانی ساخته شد:\n{target}\n\nSecret/Token و queue.db داخل بسته قرار نگرفته‌اند.", "Sokna Print Agent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("ساخت بسته پشتیبانی انجام نشد: " + Safe(ex.Message), "Sokna Print Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private LocalHealthSnapshot? ReadHealth()
    {
        try
        {
            if (!File.Exists(_paths.HealthPath)) return null;
            return JsonSerializer.Deserialize<LocalHealthSnapshot>(File.ReadAllText(_paths.HealthPath), AgentOptions.JsonOptions());
        }
        catch { return null; }
    }

    private static bool IsReady(PrinterQueueHealth p) => !p.Offline && !p.Paused && !p.PaperOut && !p.Error;

    private static string? GetServiceStatus(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status.ToString();
        }
        catch { return null; }
    }

    private static string? ResolveInstallRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey, false);
            return key?.GetValue("InstallRoot") as string;
        }
        catch { return null; }
    }

    private static string SetupLogsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sokna", "PrintAgentSetup", "logs");

    private static IEnumerable<string> ReadTail(string path, int maxLines)
    {
        var queue = new Queue<string>(maxLines);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (queue.Count == maxLines) queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void StartShell(string command) => Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });

    private static string Capture(string fileName, string arguments, int timeoutMs = 5000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return "process start failed";
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return "timeout";
            }
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            return Safe(output + Environment.NewLine + error, 20000);
        }
        catch (Exception e) { return Safe(e.Message); }
    }

    private static void CopyIfExists(string source, string target)
    {
        if (File.Exists(source)) File.Copy(source, target, true);
    }

    private static string? GetJsonString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static string Safe(string value, int max = 400) => value.Length > max ? value[..max] : value;

    private enum HealthTone { Healthy, Warning, Error }
    private sealed record TestResult(bool Success, string Detail)
    {
        public static TestResult Pass(string detail) => new(true, detail);
        public static TestResult Fail(string detail) => new(false, detail);
    }
    public sealed record PrinterRow(string Name, string Status, int Jobs, string Driver, string Port)
    {
        public PrinterRow(PrinterQueueHealth p) : this(p.Name, p.Offline ? "Offline" : p.PaperOut ? "Paper Out" : p.Paused ? "Paused" : p.Error ? "Error" : "Ready", p.Jobs, p.Driver, p.Port) { }
    }
    public sealed record TestRow(string Name, string Status, string Detail, string Duration);
    public sealed record LogRow(string Time, string Level, string Area, string Message);
}
