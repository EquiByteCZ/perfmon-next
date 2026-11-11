using System.Windows;
using PerfMonNext.Core;
using PerfMonNext.Windows;

namespace PerfMonNext;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "PerfMon Next", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var vault = new MemoryCredentialVault();
        var protector = new DpapiSecretProtector();
        AppFiles.TryImportCredentials(vault, protector);
        var probe = new WindowsCounterReader(vault);
        var engine = new SamplingEngine(new GuardedCounterPoller(probe));
        var vm = new MainViewModel(engine, probe, new WindowsCounterCatalog(vault), vault, protector, new RelogBlgConverter());
        new MainWindow(vm).Show();
    }
}
