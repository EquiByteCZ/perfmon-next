using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PerfMonNext.Core;
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

    private async void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        await _vm.AddMachineAsync(MachinePassword.Password);
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

    private int _lastCounterIndex = -1;
    private int _lastInstanceIndex = -1;

    private void CounterList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = IndexOfListItem(CounterList, e);
        if (index < 0)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastCounterIndex >= 0)
        {
            var rows = IndexRange.Inclusive(_lastCounterIndex, index)
                .Where(i => i >= 0 && i < _vm.Counters.Count)
                .Select(i => _vm.Counters[i])
                .ToList();
            _vm.SetCountersChecked(rows, !_vm.Counters[index].IsChecked);
            e.Handled = true;
            return;
        }

        _lastCounterIndex = index;
    }

    private void CounterList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SelectAllCountersCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void InstanceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = IndexOfListItem(InstanceList, e);
        if (index < 0)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastInstanceIndex >= 0)
        {
            var rows = IndexRange.Inclusive(_lastInstanceIndex, index)
                .Where(i => i >= 0 && i < _vm.Instances.Count)
                .Select(i => _vm.Instances[i])
                .ToList();
            _vm.SetInstancesChecked(rows, !_vm.Instances[index].IsChecked);
            e.Handled = true;
            return;
        }

        _lastInstanceIndex = index;
    }

    private void InstanceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SelectAllInstancesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RemoveSelectedSeries_Click(object sender, RoutedEventArgs e) => RemoveSelectedSeries();

    private void SeriesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedSeries();
            e.Handled = true;
        }
    }

    private void RemoveSelectedSeries()
    {
        var keys = SeriesGrid.SelectedItems.OfType<SeriesRowViewModel>().Select(row => row.Key).ToList();
        _vm.RemoveSeriesMany(keys);
    }

    private static int IndexOfListItem(ListBox list, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        return item is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(item);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start is not null)
        {
            if (start is T match)
                return match;
            start = VisualTreeHelper.GetParent(start);
        }

        return null;
    }
}
