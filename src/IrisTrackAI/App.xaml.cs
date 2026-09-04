using System.Windows;

namespace IrisTrackAI;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
