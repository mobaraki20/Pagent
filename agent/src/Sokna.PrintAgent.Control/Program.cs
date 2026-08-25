using System.Windows;

namespace Sokna.PrintAgent.Control;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        app.Run(new MainWindow());
    }
}
