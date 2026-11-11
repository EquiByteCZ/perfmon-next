using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace PerfMonNext;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void ToggleAddMachine(object sender, RoutedEventArgs e) => _vm.IsAddMachineOpen = !_vm.IsAddMachineOpen;

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        _vm.AddMachine(MachinePassword.Password);
        MachinePassword.Clear();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenWorkspace();

    private void OpenExecuted(object sender, ExecutedRoutedEventArgs e) => OpenWorkspace();

    private void OpenWorkspace()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Workspace (*.json)|*.json|All files|*.*",
            Title = "Open workspace"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                _vm.OpenWorkspace(dialog.FileName);
            }
            catch (Exception ex)
            {
                _vm.Banner = ex.Message;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveWorkspace();

    private void SaveExecuted(object sender, ExecutedRoutedEventArgs e) => SaveWorkspace();

    private void SaveWorkspace()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Workspace (*.json)|*.json",
            Title = "Save workspace",
            FileName = "workspace.json"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                _vm.SaveWorkspace(dialog.FileName);
            }
            catch (Exception ex)
            {
                _vm.Banner = ex.Message;
            }
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            Title = "Export samples",
            FileName = "samples.csv"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                _vm.ExportCsv(dialog.FileName);
            }
            catch (Exception ex)
            {
                _vm.Banner = ex.Message;
            }
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Counter data (*.csv;*.blg)|*.csv;*.blg|CSV|*.csv|Performance log (*.blg)|*.blg",
            Title = "Import samples"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            await _vm.ImportDataAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            _vm.Banner = ex.Message;
        }
    }

    private void ChartScroll_SizeChanged(object sender, SizeChangedEventArgs e) => _vm.SetChartViewport(e.NewSize.Height);
}
