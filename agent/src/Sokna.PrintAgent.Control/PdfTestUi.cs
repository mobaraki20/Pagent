using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sokna.PrintAgent.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace Sokna.PrintAgent.Control;

public partial class MainWindow
{
    private bool _pdfControlsInitialized;
    private bool _pdfModeUpdating;
    private CheckBox? _pdfModeToggle;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if(_pdfControlsInitialized)return;
        _pdfControlsInitialized=true;

        var actions=TestsPage.Children
            .OfType<StackPanel>()
            .FirstOrDefault(x=>Grid.GetRow(x)==2);
        if(actions is null)return;

        _pdfModeToggle=new CheckBox
        {
            Content="حالت تست PDF E2E",
            Margin=new Thickness(14,0,0,0),
            VerticalAlignment=VerticalAlignment.Center,
            VerticalContentAlignment=VerticalAlignment.Center,
            ToolTip="فقط برای QA/UAT. در حالت عادی خاموش است و PDF در لیست مقصدهای چاپ ظاهر نمی‌شود."
        };
        _pdfModeToggle.IsChecked=PdfTestModePolicy.IsEnabled(_paths.ConfigPath);
        _pdfModeToggle.Checked+=PdfTestModeChanged;
        _pdfModeToggle.Unchecked+=PdfTestModeChanged;
        actions.Children.Add(_pdfModeToggle);

        var export=new Button
        {
            Content="ذخیره آخرین PDF تست…",
            Margin=new Thickness(10,0,0,0),
            Style=(Style)FindResource("SecondaryButton")
        };
        export.Click+=ExportLatestPdfTest_Click;
        actions.Children.Add(export);
    }

    private void PdfTestModeChanged(object sender,RoutedEventArgs e)
    {
        if(_pdfModeUpdating||_pdfModeToggle is null)return;
        var requested=_pdfModeToggle.IsChecked==true;
        try
        {
            if(!File.Exists(_paths.ConfigPath))
                throw new InvalidOperationException("ابتدا اتصال Agent را تنظیم و config.json را ذخیره کنید.");

            var existing=AgentOptions.Load(_paths.ConfigPath);
            if(existing.PdfTestSinkEnabled==requested)return;
            (existing with{PdfTestSinkEnabled=requested}).Save(_paths.ConfigPath);
            RestartServiceIfRunning();
            RefreshEverything();

            MessageBox.Show(
                requested
                    ? "حالت تست PDF فعال شد. Queue آزمایشی فقط برای UAT/QA در دسترس است. بعد از تست آن را خاموش کنید."
                    : "حالت تست PDF خاموش شد. Queue مجازی دیگر advertise نمی‌شود و Worker نیز مقصد PDF قدیمی را رد می‌کند.",
                "Sokna Print Agent",
                MessageBoxButton.OK,
                requested?MessageBoxImage.Warning:MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            _pdfModeUpdating=true;
            try{_pdfModeToggle.IsChecked=PdfTestModePolicy.IsEnabled(_paths.ConfigPath);}finally{_pdfModeUpdating=false;}
            MessageBox.Show(ex.Message,"Sokna Print Agent",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private static void RestartServiceIfRunning()
    {
        using var service=new ServiceController("SoknaPrintAgent6");
        try
        {
            service.Refresh();
            if(service.Status==ServiceControllerStatus.Stopped)return;
            if(service.Status!=ServiceControllerStatus.StopPending)service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20));
        }
        catch(InvalidOperationException)
        {
            // Service is not installed yet; config change remains durable for the next start.
        }
    }

    private void ExportLatestPdfTest_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var directory=Path.Combine(_paths.ProgramDataRoot,"TestPrints");
            if(!Directory.Exists(directory))
                throw new FileNotFoundException("هنوز PDF آزمایشی ساخته نشده است.");

            var latest=new DirectoryInfo(directory)
                .EnumerateFiles("*.pdf",SearchOption.TopDirectoryOnly)
                .OrderByDescending(x=>x.LastWriteTimeUtc)
                .FirstOrDefault()
                ??throw new FileNotFoundException("هنوز PDF آزمایشی ساخته نشده است.");

            var dialog=new SaveFileDialog
            {
                Title="ذخیره آخرین PDF تست Sokna",
                Filter="PDF file (*.pdf)|*.pdf",
                FileName=latest.Name,
                AddExtension=true,
                DefaultExt=".pdf",
                OverwritePrompt=true
            };
            if(dialog.ShowDialog(this)!=true)return;
            File.Copy(latest.FullName,dialog.FileName,true);
        }
        catch(Exception ex)
        {
            MessageBox.Show(ex.Message,"Sokna Print Agent",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private void OpenPdfTests_Click(object sender,RoutedEventArgs e)
        =>OpenFolder(Path.Combine(_paths.ProgramDataRoot,"TestPrints"));
}
