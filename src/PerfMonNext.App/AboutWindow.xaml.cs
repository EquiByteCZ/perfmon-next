using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace PerfMonNext;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var info = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var version = plus > 0 ? info[..plus] : info;
        var commit = plus > 0 && info.Length > plus + 8 ? info.Substring(plus + 1, 7) : "";
        VersionText.Text = "Version " + version + (commit.Length > 0 ? " (" + commit + ")" : "") + " · © 2026 EquiByte s.r.o.";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser or mail handler registered; nothing else to do.
        }

        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
