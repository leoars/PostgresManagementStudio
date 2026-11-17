using System.Windows;
namespace PGMS.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReporter.Install(this);
        base.OnStartup(e);
    }
}
