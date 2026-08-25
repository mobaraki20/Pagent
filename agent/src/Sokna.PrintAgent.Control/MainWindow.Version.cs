using System.Windows;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Control;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is MainWindow window)
                    window.VersionText.Text = $"Agent {AgentVersionInfo.Current}";
            }));
    }
}
