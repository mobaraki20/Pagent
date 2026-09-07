using WpfApplication = System.Windows.Application;
using WpfShutdownMode = System.Windows.ShutdownMode;

namespace Sokna.PrintAgent.Control;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new WpfApplication
        {
            ShutdownMode = WpfShutdownMode.OnExplicitShutdown
        };
        app.Run(new MainWindow());
    }
}
