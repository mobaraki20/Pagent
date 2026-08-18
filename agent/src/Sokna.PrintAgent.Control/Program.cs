using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Sokna.PrintAgent.Core;
namespace Sokna.PrintAgent.Control;

internal static class Program
{
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new MainForm());}
}

public sealed class MainForm:Form
{
    private readonly AgentPaths _paths=AgentPaths.Default();
    private readonly TextBox _url=new(){Dock=DockStyle.Top,PlaceholderText="https://example.com/cafe"};
    private readonly TextBox _token=new(){Dock=DockStyle.Top,PlaceholderText="Agent token (برای تعویض/ثبت)",UseSystemPasswordChar=true};
    private readonly Label _status=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};
    private readonly ListBox _printers=new(){Dock=DockStyle.Fill};
    private readonly Button _test=new(){Text="تست اتصال API v4",Dock=DockStyle.Top,Height=42};

    public MainForm()
    {
        Text="Sokna Print Agent 6";Width=760;Height=560;RightToLeft=RightToLeft.Yes;RightToLeftLayout=true;
        var save=new Button{Text="ذخیره تنظیمات",Dock=DockStyle.Top,Height=42};save.Click+=Save;
        var refresh=new Button{Text="بررسی سلامت Service",Dock=DockStyle.Top,Height=42};refresh.Click+=(_,_)=>RefreshHealth();
        _test.Click+=async(_,_)=>await TestApiAsync();
        var logs=new Button{Text="بازکردن پوشه Logs",Dock=DockStyle.Top,Height=38};logs.Click+=(_,_)=>OpenLogs();
        var panel=new Panel{Dock=DockStyle.Top,Height=230,Padding=new Padding(12)};panel.Controls.Add(logs);panel.Controls.Add(refresh);panel.Controls.Add(_test);panel.Controls.Add(save);panel.Controls.Add(_token);panel.Controls.Add(_url);
        Controls.Add(_printers);Controls.Add(_status);Controls.Add(panel);LoadExisting();RefreshHealth();
    }

    private void LoadExisting(){try{if(File.Exists(_paths.ConfigPath)){var o=AgentOptions.Load(_paths.ConfigPath);_url.Text=o.ServerBaseUrl;}}catch(Exception e){_status.Text="Config: "+Safe(e.Message);}}

    private void Save(object? sender,EventArgs e)
    {
        try
        {
            var opt=new AgentOptions{ServerBaseUrl=_url.Text.Trim(),AgentName=Environment.MachineName};opt.Validate();opt.Save(_paths.ConfigPath);
            if(!string.IsNullOrWhiteSpace(_token.Text))SecretStore.Save(_paths.SecretPath,_token.Text);_token.Clear();
            _status.Text="تنظیمات ذخیره شد. Service تغییرات را خودکار بارگذاری می‌کند؛ Restart لازم نیست.";
        }
        catch(Exception ex){MessageBox.Show(Safe(ex.Message),"خطا",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }

    private async Task TestApiAsync()
    {
        _test.Enabled=false;
        try
        {
            var opt=AgentOptions.Load(_paths.ConfigPath);opt.Validate();var token=SecretStore.Load(_paths.SecretPath);
            using var http=new HttpClient();var api=new HttpPrintTransport(http,opt.ServerBaseUrl,token);var probe=await api.ProbeAsync(CancellationToken.None);
            _status.Text=probe.Success&&probe.ProtocolVersion==4?$"API v4 آماده است · {probe.Destinations.Count} مقصد قابل مشاهده":"API پاسخ داد ولی Protocol مورد انتظار فعال نیست.";
        }
        catch(Exception e){_status.Text="API: "+Safe(e.Message);}
        finally{_test.Enabled=true;}
    }

    private void RefreshHealth()
    {
        _printers.Items.Clear();
        try
        {
            if(File.Exists(_paths.HealthPath))
            {
                var snapshot=JsonSerializer.Deserialize<LocalHealthSnapshot>(File.ReadAllText(_paths.HealthPath),AgentOptions.JsonOptions());
                if(snapshot is not null)
                {
                    foreach(var q in snapshot.Printers)_printers.Items.Add($"{q.Name} — {(q.Offline?"Offline":q.PaperOut?"Paper Out":q.Paused?"Paused":q.Error?"Error":"Ready")} — {q.Jobs} job — {q.Port}");
                    _status.Text=$"Service snapshot: {snapshot.State} | Config: {(snapshot.ConfigOk?"OK":"Missing/Invalid")} | Token: {(snapshot.SecretOk?"OK":"Missing")} | Account: {(snapshot.ServiceAccountContext?"LocalSystem":"بررسی شود")} | Updated: {snapshot.UpdatedAt}";
                }
            }
            else _status.Text="health.json هنوز توسط Service ساخته نشده است.";
            using var sc=new ServiceController("SoknaPrintAgent6");_status.Text+=$" | Service: {sc.Status}";
        }
        catch(Exception e){_status.Text="Health: "+Safe(e.Message);}
    }

    private void OpenLogs(){try{Directory.CreateDirectory(_paths.LogsPath);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{_paths.LogsPath}\""){UseShellExecute=true});}catch(Exception e){_status.Text="Logs: "+Safe(e.Message);}}
    private static string Safe(string s)=>s.Length>400?s[..400]:s;
}
