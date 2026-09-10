using System.IO;
using System.Windows;

namespace Sokna.PrintAgent.Control;

public partial class MainWindow
{
    private void OpenPdfTests_Click(object sender,RoutedEventArgs e)
        =>OpenFolder(Path.Combine(_paths.ProgramDataRoot,"TestPrints"));
}
