using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfMonNext.Core;
using PerfMonNext.Windows;

namespace PerfMonNext;

public sealed record Choice<T>(T Value, string Label);

public partial class MainViewModel : ObservableObject
{
    private readonly SamplingEngine _engine;
    private readonly ICounterProbe _probe;
    private readonly WindowsCounterCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly ISecretProtector _protector;
    private readonly RelogBlgConverter _relog;
    private readonly IClusterNodeLookup? _clusters;
    private readonly SeriesStore _store = new();
    private int _bulk;
    private DispatcherTimer? _timer;
    private int _ticking;
    private Workspace _workspace = WorkspaceFactory.CreateNew();
    private CounterCatalog _currentCatalog = StarterCatalog.Create();
    private List<SeriesSnapshot> _last = [];
    private AppSettings _settings = new();
    private double _chartViewportHeight = 360;

    public MainViewModel(
        SamplingEngine engine,
        ICounterProbe probe,
        WindowsCounterCatalog catalog,
        ICredentialVault vault,
        ISecretProtector protector,
        RelogBlgConverter relog,
        IClusterNodeLookup? clusters = null)
    {
        _engine = engine;
        _probe = probe;
        _catalog = catalog;
        _vault = vault;
        _protector = protector;
        _relog = relog;
        _clusters = clusters;
        _settings = AppFiles.LoadSettings();
        if (_settings.LastWorkspacePath is string lastWorkspace && File.Exists(lastWorkspace))
        {
            try
            {
                OpenWorkspace(lastWorkspace);
                return;
            }
            catch (Exception ex)
            {
                Banner = ex.Message;
            }
        }

        ApplyWorkspaceToUi();
        ShowCatalog(StarterCatalog.Create());
        _ = ReloadCatalogAsync();
    }

    public ObservableCollection<MachineItemViewModel> Machines { get; } = [];
    public ObservableCollection<CounterSetInfo> CatalogSets { get; } = [];
    public ObservableCollection<CounterRowViewModel> Counters { get; } = [];
    public ObservableCollection<InstanceRowViewModel> Instances { get; } = [];
    public ObservableCollection<SeriesRowViewModel> SeriesRows { get; } = [];
    public ObservableCollection<ChartViewModel> Charts { get; } = [];
    public ObservableCollection<Choice<int>> IntervalChoices { get; } =
    [
        new(1000, "1 s"), new(2000, "2 s"), new(5000, "5 s"),
        new(10000, "10 s"), new(30000, "30 s"), new(60000, "60 s")
    ];
    public ObservableCollection<Choice<int>> WindowChoices { get; } =
    [
        new(60, "1 min"), new(300, "5 min"), new(900, "15 min"), new(3600, "1 hour"), new(0, "All")
    ];
    public IReadOnlyList<Choice<string>> ChartModeChoices { get; } =
    [
        new("ByCounter", "By counter"),
        new("ByMachine", "By machine"),
        new("Combined", "Combined"),
        new("off", "Table only")
    ];

    [ObservableProperty] private string workspaceName = "Local overview";
    [ObservableProperty] private string windowTitle = "PerfMon Next";
    [ObservableProperty] private int sampleIntervalMs = 1000;
    [ObservableProperty] private int windowSeconds = 300;
    [ObservableProperty] private ChartGrouping grouping = ChartGrouping.ByCounter;
    [ObservableProperty] private string chartMode = "ByCounter";
    [ObservableProperty] private bool followLive = true;
    [ObservableProperty] private bool showLegend = true;
    [ObservableProperty] private bool chartsVisible = true;
    [ObservableProperty] private bool isSampling;
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool isAddMachineOpen;
    [ObservableProperty] private bool isCatalogLoading;
    [ObservableProperty] private bool scopeAllMachines = true;
    [ObservableProperty] private string statusText = "Stopped";
    [ObservableProperty] private string banner = "";
    [ObservableProperty] private string catalogBanner = "";
    [ObservableProperty] private string instanceBanner = "";
    [ObservableProperty] private string emptyMessage = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string instanceFilter = "";
    [ObservableProperty] private string helpText = "Select a counter to read its explain text.";
    [ObservableProperty] private string pathText = "";
    [ObservableProperty] private string newHost = "";
    [ObservableProperty] private string newDisplayName = "";
    [ObservableProperty] private string newUserName = "";
    [ObservableProperty] private bool useAlternateCredential;
    [ObservableProperty] private bool rememberCredential;
    [ObservableProperty] private MachineItemViewModel? selectedMachine;
    [ObservableProperty] private CounterSetInfo? selectedSet;
    [ObservableProperty] private CounterRowViewModel? selectedCounter;

    public double ChartItemHeight => ChartSizing.Default(Charts.Count, _chartViewportHeight);
    public GridLength ChartRowHeight => ChartsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public GridLength SplitterRowHeight => ChartsVisible ? new GridLength(6) : new GridLength(0);
    public GridLength TableRowHeight => ChartsVisible ? new GridLength(230) : new GridLength(1, GridUnitType.Star);
    public string CurrentScopeId => ScopeAllMachines || SelectedMachine is null ? "*" : SelectedMachine.Id;
    public string ScopeHint => ScopeAllMachines
        ? "A tick adds the counter on every machine."
        : "A tick adds the counter only on " + (SelectedMachine?.DisplayName ?? "the selected machine") + ".";

    public void SetChartViewport(double height)
    {
        if (Math.Abs(_chartViewportHeight - height) < 1)
            return;
        _chartViewportHeight = height;
        OnPropertyChanged(nameof(ChartItemHeight));
        UpdateChartDefaults();
    }

    private void UpdateChartDefaults()
    {
        var height = ChartItemHeight;
        foreach (var chart in Charts)
            chart.DefaultHeight = height;
    }

    /// <summary>Live resize while the user drags a chart's bottom edge.</summary>
    public void ResizeChart(ChartViewModel chart, double delta)
    {
        var height = ChartSizing.Drag(chart.DisplayHeight, delta);
        chart.CustomHeight = height;
        // Write through at once so the next refresh (which re-reads saved heights) does not snap it back mid-drag.
        _workspace.ChartHeights[chart.Key] = height;
    }

    /// <summary>Remember the dragged height in the workspace so a saved workspace keeps it.</summary>
    public void CommitChartHeight(ChartViewModel chart)
    {
        if (chart.CustomHeight is { } height)
            _workspace.ChartHeights[chart.Key] = height;
        else
            _workspace.ChartHeights.Remove(chart.Key);
    }

    /// <summary>Double-click on the resize handle: back to the default height.</summary>
    public void ResetChartHeight(ChartViewModel chart)
    {
        chart.CustomHeight = null;
        _workspace.ChartHeights.Remove(chart.Key);
    }

    partial void OnWorkspaceNameChanged(string value)
    {
        _workspace.Name = string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim();
        WindowTitle = "PerfMon Next — " + _workspace.Name;
    }

    partial void OnSampleIntervalMsChanged(int value)
    {
        _workspace.SampleIntervalMs = value;
        if (_timer is not null)
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(WorkspaceSanitizer.MinIntervalMs, value));
    }

    partial void OnWindowSecondsChanged(int value)
    {
        _workspace.VisibleWindowSeconds = value;
        RefreshPresentation();
    }

    partial void OnGroupingChanged(ChartGrouping value)
    {
        _workspace.ChartGrouping = value;
        RefreshPresentation();
    }

    partial void OnChartModeChanged(string value)
    {
        if (value == "off")
        {
            _workspace.ChartsEnabled = false;
            ChartsVisible = false;
            NotifyChartLayout();
            RefreshPresentation();
            return;
        }

        _workspace.ChartsEnabled = true;
        ChartsVisible = true;
        Grouping = value switch
        {
            "ByMachine" => ChartGrouping.ByMachine,
            "Combined" => ChartGrouping.Combined,
            _ => ChartGrouping.ByCounter
        };
        NotifyChartLayout();
        RefreshPresentation();
    }

    private void NotifyChartLayout()
    {
        OnPropertyChanged(nameof(ChartRowHeight));
        OnPropertyChanged(nameof(SplitterRowHeight));
        OnPropertyChanged(nameof(TableRowHeight));
        OnPropertyChanged(nameof(ChartItemHeight));
    }

    private static string ChartModeFor(ChartGrouping grouping, bool chartsEnabled) =>
        chartsEnabled
            ? grouping switch
            {
                ChartGrouping.ByMachine => "ByMachine",
                ChartGrouping.Combined => "Combined",
                _ => "ByCounter"
            }
            : "off";

    partial void OnFollowLiveChanged(bool value) => _workspace.FollowLive = value;
    partial void OnShowLegendChanged(bool value)
    {
        _workspace.ShowLegend = value;
        RefreshPresentation();
    }

    partial void OnSearchTextChanged(string value) => RebuildSets();
    partial void OnInstanceFilterChanged(string value) => RebuildInstances();
    partial void OnScopeAllMachinesChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeHint));
        RebuildCounters();
    }

    partial void OnSelectedMachineChanged(MachineItemViewModel? value)
    {
        OnPropertyChanged(nameof(ScopeHint));
        if (!ScopeAllMachines)
            RebuildCounters();
    }

    partial void OnSelectedSetChanged(CounterSetInfo? value) => RebuildCounters();
    partial void OnSelectedCounterChanged(CounterRowViewModel? value) => RebuildInstances();

    [RelayCommand]
    private void Start()
    {
        if (_timer is not null)
            return;
        if (IsOffline)
        {
            _store.Clear();
            IsOffline = false;
            Banner = "";
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(WorkspaceSanitizer.MinIntervalMs, SampleIntervalMs)) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        IsSampling = true;
        Tick();
    }

    [RelayCommand]
    private void Stop()
    {
        if (_timer is null)
            return;
        _timer.Stop();
        _timer = null;
        _engine.Stop();
        IsSampling = false;
        StatusText = "Stopped · history kept";
    }

    [RelayCommand]
    private void NewWorkspace()
    {
        Stop();
        _store.Clear();
        IsOffline = false;
        _workspace = WorkspaceFactory.CreateNew();
        Banner = "";
        ApplyWorkspaceToUi();
        _ = ReloadCatalogAsync();
    }

    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        var machine = SelectedTarget();
        if (machine is null)
            return;
        IsCatalogLoading = true;
        CatalogBanner = "Loading counter sets from " + MachineHost.Display(machine.Host, machine.DisplayName) + "…";
        try
        {
            var loaded = await Task.Run(() => _catalog.Load(machine, CancellationToken.None)).ConfigureAwait(true);
            ShowCatalog(loaded);
        }
        catch (Exception ex)
        {
            CatalogBanner = ex.Message + " The starter list is still available.";
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    [RelayCommand]
    private void UseStarterCatalog() => ShowCatalog(StarterCatalog.Create());

    [RelayCommand]
    private async Task RetryInstancesAsync()
    {
        await ReloadCatalogAsync();
    }


    [RelayCommand]
    private async Task ProbeAsync()
    {
        var machine = SelectedTarget();
        if (machine is null)
            return;
        var item = SelectedMachine;
        if (item is not null)
        {
            item.Status = MachineStatus.Busy;
            item.StatusText = "Reading";
            item.Detail = "Checking Memory\\Available MBytes…";
        }

        var read = Task.Run(() => _probe.Read(machine, [new CounterPath("Memory", "Available MBytes", "")]));
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(true);
        if (finished != read)
        {
            if (item is not null)
            {
                item.Status = MachineStatus.Timeout;
                item.StatusText = "Waiting";
                item.Detail = "The probe is still running. Sampling will not start a second read until it finishes.";
            }
            return;
        }

        ApplyResult(await read.ConfigureAwait(true));
    }

    [RelayCommand]
    private void RemoveMachine()
    {
        if (SelectedMachine is null)
            return;
        var id = SelectedMachine.Id;
        WorkspaceEditor.RemoveMachine(_workspace, id);
        _vault.Remove(id);
        _probe.Release(id);
        _engine.Stop();
        PersistCredentials();
        Machines.Remove(SelectedMachine);
        SelectedMachine = Machines.FirstOrDefault();
        _store.Prune(_workspace);
        RefreshPresentation();
    }

    [RelayCommand]
    private void ResetView()
    {
        // Back to the default view: every scale ×1, every series shown, auto zoom (Y from 0), following live.
        // Series and machines are kept.
        _workspace.ScaleOverrides.Clear();
        _workspace.Visibility.Clear();
        _workspace.ChartHeights.Clear();
        foreach (var chart in Charts)
            chart.CustomHeight = null;
        FollowLive = true;
        RefreshPresentation();
        Banner = "View reset: all scales ×1, all series shown, charts back to live auto-zoom.";
    }

    [RelayCommand]
    private void FitScales()
    {
        var plan = ScaleFitter.Fit(_last, _workspace.ChartGrouping);
        var scaled = 0;
        foreach (var (key, factor) in plan)
        {
            if (ScaleFormat.IsIdentity(factor))
                _workspace.ScaleOverrides.Remove(key);
            else
            {
                _workspace.ScaleOverrides[key] = factor;
                scaled++;
            }
        }

        RefreshPresentation();
        Banner = scaled == 0
            ? "Fit scales: nothing to fit. Each chart's series are already within 10× of each other (a single series always stays ×1)."
            : "Fit scales: magnified " + scaled + " smaller series; see ×factor in the Scale column and legend. Hover shows real values. Reset sets all back to ×1.";
    }

    [RelayCommand]
    private void AddPath()
    {
        if (string.IsNullOrWhiteSpace(PathText))
            return;
        try
        {
            WorkspaceEditor.AddCounterPath(_workspace, PathText.Trim(), CurrentScopeId);
            PathText = "";
            BindMachines();
            RebuildCounters();
            Banner = "";
        }
        catch (FormatException ex)
        {
            Banner = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveSeries(string? key)
    {
        var snapshot = _last.FirstOrDefault(item => item.Key == key);
        if (snapshot?.SelectionId is not string id)
            return;
        _workspace.Counters.RemoveAll(counter => counter.Id == id);
        _store.Prune(_workspace);
        RebuildCounters();
        RefreshPresentation();
    }

    public int CounterSelectionCount => _workspace.Counters.Count;

    /// <summary>Removes every counter and series (machines stay) and unticks the picker.</summary>
    public void ClearAllCounters()
    {
        var removed = WorkspaceEditor.ClearCounters(_workspace);
        if (removed == 0)
            return;
        _store.Prune(_workspace);
        RebuildCounters();
        RebuildInstances();
        RefreshPresentation();
        Banner = "Removed all " + removed + " counter selection" + (removed == 1 ? "" : "s") + ". Machines are unchanged.";
    }

    public void RemoveSeriesMany(IEnumerable<string> keys)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var snapshot = _last.FirstOrDefault(item => item.Key == key);
            if (snapshot?.SelectionId is string id)
                ids.Add(id);
        }

        if (ids.Count == 0)
            return;
        _workspace.Counters.RemoveAll(counter => ids.Contains(counter.Id));
        _store.Prune(_workspace);
        RebuildCounters();
        RefreshPresentation();
    }

    [RelayCommand]
    private void CycleColor(string? key)
    {
        var snapshot = _last.FirstOrDefault(item => item.OverrideKey == key || item.Key == key);
        if (snapshot is null)
            return;
        _workspace.ColorOverrides[snapshot.OverrideKey] = SeriesPalette.Next(snapshot.Color);
        RefreshPresentation();
    }

    public void AddMachine(string password) => _ = AddMachineAsync(password);

    public ObservableCollection<ClusterNodeRowViewModel> ClusterNodes { get; } = [];
    [ObservableProperty] private bool addAsCluster;
    [ObservableProperty] private bool isClusterBusy;
    [ObservableProperty] private string clusterMessage = "";
    [ObservableProperty] private bool clusterMessageIsError;
    private string? _foundClusterName;
    private string? _foundClusterHost;
    private CancellationTokenSource? _clusterCts;

    public bool AddAsServer
    {
        get => !AddAsCluster;
        set => AddAsCluster = !value;
    }

    public bool HasClusterNodes => ClusterNodes.Count > 0;
    public int SelectedClusterNodeCount => ClusterNodes.Count(node => node.IsChecked && !node.AlreadyAdded);
    public string AddClusterNodesText => SelectedClusterNodeCount switch
    {
        0 => "No new nodes selected",
        1 => "Add 1 node",
        var n => "Add " + n + " nodes"
    };
    public string AddButtonText => AddAsCluster ? (IsClusterBusy ? "Reading cluster…" : "Find nodes") : "Add machine";

    partial void OnAddAsClusterChanged(bool value)
    {
        OnPropertyChanged(nameof(AddAsServer));
        OnPropertyChanged(nameof(AddButtonText));
        ResetClusterDiscovery();
    }

    partial void OnIsClusterBusyChanged(bool value) => OnPropertyChanged(nameof(AddButtonText));

    partial void OnNewHostChanged(string value)
    {
        if (_foundClusterHost is not null && !string.Equals(_foundClusterHost, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ResetClusterDiscovery();
    }

    partial void OnIsAddMachineOpenChanged(bool value)
    {
        if (!value)
            ResetClusterDiscovery();
    }

    internal void OnClusterNodeToggled()
    {
        OnPropertyChanged(nameof(SelectedClusterNodeCount));
        OnPropertyChanged(nameof(AddClusterNodesText));
    }

    [RelayCommand]
    private void SelectAllClusterNodes() => SetAllClusterNodes(true);

    [RelayCommand]
    private void SelectNoClusterNodes() => SetAllClusterNodes(false);

    private void SetAllClusterNodes(bool value)
    {
        foreach (var node in ClusterNodes.Where(node => !node.AlreadyAdded))
            node.IsChecked = value;
        OnClusterNodeToggled();
    }

    private void ResetClusterDiscovery()
    {
        _clusterCts?.Cancel();
        _clusterCts = null;
        IsClusterBusy = false;
        ClusterNodes.Clear();
        _foundClusterName = null;
        _foundClusterHost = null;
        ClusterMessage = "";
        ClusterMessageIsError = false;
        OnPropertyChanged(nameof(HasClusterNodes));
        OnClusterNodeToggled();
    }

    /// <summary>Primary button of the Add panel: adds a server, or reads the cluster's node list.</summary>
    public async Task AddMachineAsync(string password)
    {
        if (AddAsCluster)
        {
            await FindClusterNodesAsync(password).ConfigureAwait(true);
            return;
        }

        var host = string.IsNullOrWhiteSpace(NewHost) ? "." : NewHost.Trim();
        if (!CheckCredentialInput(host))
            return;

        var customName = string.IsNullOrWhiteSpace(NewDisplayName) ? null : NewDisplayName.Trim();
        if (_workspace.Machines.FirstOrDefault(machine => MachineHost.SameHost(machine.Host, host)) is { } already)
        {
            Banner = MachineHost.Display(already.Host, already.DisplayName) + " is already in the list.";
            SelectedMachine = Machines.FirstOrDefault(machine => machine.Id == already.Id);
            return;
        }

        var added = AddTargets([host], customName, null, password);
        FinishAdd(added);
        Banner = "";
    }

    public async Task FindClusterNodesAsync(string password)
    {
        var host = NewHost?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(host) || MachineHost.IsLocal(host))
        {
            ClusterMessage = "Enter the cluster name (or the name of any node in the cluster).";
            ClusterMessageIsError = true;
            return;
        }

        if (!CheckCredentialInput(host))
            return;
        if (_clusters is null)
        {
            ClusterMessage = "Cluster discovery is not available in this build.";
            ClusterMessageIsError = true;
            return;
        }

        ResetClusterDiscovery();
        var cts = new CancellationTokenSource();
        _clusterCts = cts;
        IsClusterBusy = true;
        ClusterMessage = "Reading the node list of " + host + "…";
        var user = UseAlternateCredential ? NewUserName.Trim() : null;
        ClusterQueryResult result;
        try
        {
            var query = Task.Run(() => _clusters.Query(host, user, password, cts.Token));
            var finished = await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(90), cts.Token)).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            result = finished == query
                ? await query.ConfigureAwait(true)
                : ClusterQueryResult.Error(ClusterQueryStatus.Unreachable, "No answer within 90 seconds.", "WMI");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = ClusterQueryResult.Error(ClusterQueryStatus.Failed, ex.Message, "WMI");
        }
        finally
        {
            if (ReferenceEquals(_clusterCts, cts))
                IsClusterBusy = false;
        }

        if (!ReferenceEquals(_clusterCts, cts))
            return;

        if (!result.Succeeded)
        {
            ClusterMessage = ClusterExpansion.Describe(result, host);
            ClusterMessageIsError = true;
            return;
        }

        var candidates = ClusterExpansion.Candidates(host, result.Nodes, _workspace.Machines.Select(machine => machine.Host));
        foreach (var candidate in candidates)
            ClusterNodes.Add(new ClusterNodeRowViewModel(this, candidate));
        _foundClusterName = result.ClusterName;
        _foundClusterHost = host;
        var already = candidates.Count(candidate => candidate.AlreadyAdded);
        ClusterMessage = ClusterExpansion.Describe(result, host)
            + (already > 0 ? " " + already + " already in the list." : "")
            + " Untick any node you do not want.";
        ClusterMessageIsError = false;
        OnPropertyChanged(nameof(HasClusterNodes));
        OnClusterNodeToggled();
    }

    public void AddClusterNodes(string password)
    {
        var host = NewHost?.Trim() ?? "";
        if (!CheckCredentialInput(host))
            return;
        var hosts = ClusterNodes.Where(node => node.IsChecked && !node.AlreadyAdded).Select(node => node.Host).ToList();
        if (hosts.Count == 0)
        {
            ClusterMessage = "Tick at least one node that is not in the list yet.";
            ClusterMessageIsError = true;
            return;
        }

        var clusterName = _foundClusterName is null ? null : MachineHost.ShortName(_foundClusterName);
        var added = AddTargets(hosts, null, clusterName, password);
        var skipped = hosts.Count - added.Count;
        FinishAdd(added);
        Banner = "Added " + added.Count + (added.Count == 1 ? " node" : " nodes")
            + (clusterName is null ? "" : " from cluster " + clusterName)
            + (skipped > 0 ? " (" + skipped + " already in the list)." : ".");
    }

    private bool CheckCredentialInput(string host)
    {
        if (UseAlternateCredential && !MachineHost.IsLocal(host) && string.IsNullOrWhiteSpace(NewUserName))
        {
            Banner = "Enter a user name for the alternate credentials.";
            if (AddAsCluster)
            {
                ClusterMessage = Banner;
                ClusterMessageIsError = true;
            }

            return false;
        }

        return true;
    }

    private List<MachineTarget> AddTargets(IReadOnlyList<string> hosts, string? customName, string? cluster, string password)
    {
        var added = new List<MachineTarget>();
        foreach (var item in hosts)
        {
            if (_workspace.Machines.Any(machine => MachineHost.SameHost(machine.Host, item)))
                continue;

            var target = new MachineTarget
            {
                Id = Ids.New("m"),
                Host = MachineHost.IsLocal(item) ? "." : item,
                DisplayName = (hosts.Count == 1 ? customName : null) ?? (cluster is null ? MachineHost.Display(item, null) : MachineHost.ShortName(item)),
                Cluster = cluster,
                Credential = new MachineCredential
                {
                    Mode = UseAlternateCredential ? CredentialMode.Alternate : CredentialMode.Current,
                    UserName = UseAlternateCredential ? NewUserName.Trim() : null
                }
            };
            _workspace.Machines.Add(target);
            if (UseAlternateCredential && !string.IsNullOrWhiteSpace(NewUserName))
                _vault.Set(target.Id, NewUserName.Trim(), password);
            added.Add(target);
        }

        return added;
    }

    private void FinishAdd(List<MachineTarget> added)
    {
        if (RememberCredential && added.Count > 0)
            PersistCredentials();

        BindMachines();
        if (added.Count > 0)
            SelectedMachine = Machines.FirstOrDefault(machine => machine.Id == added[^1].Id);

        NewHost = "";
        NewDisplayName = "";
        NewUserName = "";
        IsAddMachineOpen = false;
    }

    public void OpenWorkspace(string path)
    {
        var loaded = WorkspaceStore.Load(path);
        StopInternal();
        _store.Clear();
        IsOffline = false;
        _workspace = loaded.Workspace;
        _settings.LastWorkspacePath = path;
        AppFiles.SaveSettings(_settings);
        Banner = string.Join(" ", loaded.Warnings);
        ApplyWorkspaceToUi();
        _ = ReloadCatalogAsync();
    }

    public void SaveWorkspace(string path)
    {
        WorkspaceStore.Save(path, _workspace);
        _settings.LastWorkspacePath = path;
        AppFiles.SaveSettings(_settings);
        Banner = "Saved " + path;
    }

    public void ExportCsv(string path)
    {
        CsvExchange.Save(path, _store.Snapshot(_workspace, null));
        Banner = "Exported " + path;
    }

    public async Task ImportDataAsync(string path)
    {
        var csvPath = path;
        if (BlgImportPlanner.LooksLikeBlg(path, ReadHeader(path)))
        {
            Banner = "Converting the binary log with relog.exe…";
            csvPath = await _relog.ConvertToCsvAsync(path, CancellationToken.None).ConfigureAwait(true);
        }

        var imported = CsvExchange.Import(File.ReadAllText(csvPath));
        StopInternal();
        _workspace.Counters.Clear();
        foreach (var series in imported.Series)
        {
            var text = new CounterPath(series.Category, series.Counter, series.Instance, series.Machine).Format();
            WorkspaceEditor.AddCounterPath(_workspace, text, "*");
        }

        _store.LoadImported(imported.Series, _workspace);
        IsOffline = true;
        Banner = "Offline view of " + Path.GetFileName(path) + ". " + string.Join(" ", imported.Warnings);
        BindMachines();
        RebuildCounters();
        RefreshPresentation();
    }

    public void OnCounterToggled(CounterRowViewModel row, bool enabled)
    {
        if (SelectedSet is null)
            return;
        if (!ReferenceEquals(SelectedCounter, row))
            SelectedCounter = row;
        if (!enabled)
            WorkspaceEditor.SetInstances(_workspace, CurrentScopeId, SelectedSet.Name, row.Name, []);
        else if (!WorkspaceEditor.HasCounter(_workspace, CurrentScopeId, SelectedSet.Name, row.Name))
        {
            var instance = WorkspaceEditor.DefaultInstance(SelectedSet);
            if (instance is null)
            {
                Banner = SelectedSet.EnumerationError ?? CategoryEnumeration.MissingInstancesMessage;
                RebuildInstances();
                RefreshPresentation();
                return;
            }
            WorkspaceEditor.SetInstances(_workspace, CurrentScopeId, SelectedSet.Name, row.Name, [instance]);
        }
        if (_bulk == 0)
        {
            RebuildInstances();
            RefreshPresentation();
        }
    }

    [RelayCommand]
    private void SelectAllCounters() => SetCountersChecked(Counters.ToList(), true);

    [RelayCommand]
    private void SelectNoCounters() => SetCountersChecked(Counters.ToList(), false);

    [RelayCommand]
    private void SelectAllInstances() =>
        // "All" means every real instance; _Total and other aggregates stay as they are.
        SetInstancesChecked(Instances.Where(row => !InstanceMatcher.IsAggregate(row.Name)).ToList(), true);

    [RelayCommand]
    private void SelectNoInstances() => SetInstancesChecked(Instances.ToList(), false);

    public void SetCountersChecked(IReadOnlyList<CounterRowViewModel> rows, bool enabled)
    {
        if (rows.Count == 0 || SelectedSet is null)
            return;
        _bulk++;
        try
        {
            foreach (var row in rows)
            {
                if (row.IsChecked == enabled)
                    continue;
                row.SetChecked(enabled);
                OnCounterToggled(row, enabled);
            }
        }
        finally
        {
            _bulk--;
            if (_bulk <= 0)
            {
                _bulk = 0;
                RebuildInstances();
                RefreshPresentation();
            }
        }
    }

    public void SetInstancesChecked(IReadOnlyList<InstanceRowViewModel> rows, bool enabled)
    {
        if (SelectedSet is null || SelectedCounter is null || rows.Count == 0)
            return;

        var current = Instances.Where(item => item.IsChecked).Select(item => item.Name).ToList();
        foreach (var row in rows)
            current = WorkspaceEditor.ToggleInstance(current, row.Name, enabled).ToList();

        WorkspaceEditor.SetInstances(_workspace, CurrentScopeId, SelectedSet.Name, SelectedCounter.Name, current);
        foreach (var instance in Instances)
            instance.SetChecked(current.Contains(instance.Name, StringComparer.OrdinalIgnoreCase));
        SelectedCounter.SetChecked(current.Count > 0);
        RefreshPresentation();
    }

    public void OnInstanceToggled(InstanceRowViewModel row, bool enabled)
    {
        if (SelectedSet is null || SelectedCounter is null)
            return;
        var current = Instances.Where(item => item != row && item.IsChecked).Select(item => item.Name).ToList();
        var next = WorkspaceEditor.ToggleInstance(current, row.Name, enabled);
        WorkspaceEditor.SetInstances(_workspace, CurrentScopeId, SelectedSet.Name, SelectedCounter.Name, next);
        foreach (var instance in Instances)
            instance.SetChecked(next.Contains(instance.Name, StringComparer.OrdinalIgnoreCase));
        SelectedCounter.SetChecked(next.Count > 0);
        RefreshPresentation();
    }

    public void SetScale(string overrideKey, double scale)
    {
        if (!double.IsFinite(scale) || scale == 0)
            return;
        _workspace.ScaleOverrides[overrideKey] = scale;
        RefreshPresentation();
    }

    public void SetVisible(string overrideKey, bool visible)
    {
        _workspace.Visibility[overrideKey] = visible;
        RefreshPresentation();
    }

    public void ApplyVisibility(IReadOnlyDictionary<string, bool> plan)
    {
        if (plan.Count == 0)
            return;
        foreach (var (key, visible) in plan)
            _workspace.Visibility[key] = visible;
        RefreshPresentation();
    }

    public void SetVisibleMany(IEnumerable<SeriesRowViewModel> rows, bool visible) =>
        ApplyVisibility(SeriesVisibility.Set(rows.Select(row => row.OverrideKey), visible));

    public void ToggleVisibleMany(IEnumerable<SeriesRowViewModel> rows) =>
        ApplyVisibility(SeriesVisibility.Toggle(rows.Select(row => (row.OverrideKey, row.Visible))));

    public void ShowOnly(IEnumerable<SeriesRowViewModel> rows) =>
        ApplyVisibility(SeriesVisibility.ShowOnly(SeriesRows.Select(row => row.OverrideKey), rows.Select(row => row.OverrideKey)));

    public void ShowAllSeries() =>
        ApplyVisibility(SeriesVisibility.Set(SeriesRows.Select(row => row.OverrideKey), true));

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            return;
        try
        {
            _engine.Collect(_workspace, new Progress<MachineReadResult>(ApplyResult));
        }
        catch (Exception ex)
        {
            Banner = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void ApplyResult(MachineReadResult result)
    {
        _store.Apply(result, _workspace);
        var item = Machines.FirstOrDefault(machine => machine.Id == result.MachineId);
        if (item is not null)
        {
            item.Status = result.Status;
            item.StatusText = StatusLabel(result.Status);
            item.Detail = result.Message ?? "";
        }

        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        _last = _store.Snapshot(_workspace, _workspace.VisibleWindow).ToList();
        SyncRows(_last);
        if (!_workspace.ChartsEnabled)
        {
            if (Charts.Count > 0)
                Charts.Clear();
            EmptyMessage = "";
        }
        else
        {
            var frames = ChartFrames.Build(_last, _workspace.ChartGrouping, _workspace.ShowLegend, _workspace.VisibleWindow);
            SyncCharts(frames);
            EmptyMessage = frames.Count == 0
                ? (IsSampling ? "Waiting for a completed sample. Rate counters appear on the second read." : "Tick counters on the left, then press Start.")
                : "";
        }
        var online = Machines.Count(machine => machine.Status == MachineStatus.Online);
        var bad = Machines.Count(machine => machine.Status is MachineStatus.Offline or MachineStatus.AccessDenied or MachineStatus.Error);
        StatusText = (IsSampling ? "Sampling" : IsOffline ? "Offline data" : "Stopped")
            + " · " + Machines.Count + " machines · " + _last.Count + " series · " + online + " online"
            + (bad == 0 ? "" : " · " + bad + " not responding");
        OnPropertyChanged(nameof(ChartItemHeight));
        UpdateChartDefaults();
    }

    private void SyncRows(IReadOnlyList<SeriesSnapshot> snapshots)
    {
        var byKey = SeriesRows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            seen.Add(snapshot.Key);
            if (!byKey.TryGetValue(snapshot.Key, out var row))
            {
                row = new SeriesRowViewModel(this, snapshot.Key);
                SeriesRows.Add(row);
            }

            row.Apply(snapshot);
        }

        for (var i = SeriesRows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(SeriesRows[i].Key))
                SeriesRows.RemoveAt(i);
        }
    }

    private void SyncCharts(IReadOnlyList<ChartFrame> frames)
    {
        var existing = Charts.ToDictionary(chart => chart.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            seen.Add(frame.Key);
            if (!existing.TryGetValue(frame.Key, out var chart))
            {
                chart = new ChartViewModel(frame.Key);
                Charts.Insert(Math.Min(i, Charts.Count), chart);
            }
            else
            {
                var index = Charts.IndexOf(chart);
                if (index != i)
                    Charts.Move(index, i);
            }

            chart.Title = frame.Title;
            chart.Frame = frame;
            chart.CustomHeight = _workspace.ChartHeights.TryGetValue(frame.Key, out var saved) ? ChartSizing.Clamp(saved) : null;
        }

        for (var i = Charts.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Charts[i].Key))
                Charts.RemoveAt(i);
        }
    }

    private void ApplyWorkspaceToUi()
    {
        WorkspaceName = _workspace.Name;
        SampleIntervalMs = _workspace.SampleIntervalMs;
        WindowSeconds = _workspace.VisibleWindowSeconds;
        Grouping = _workspace.ChartGrouping;
        ChartsVisible = _workspace.ChartsEnabled;
        ChartMode = ChartModeFor(_workspace.ChartGrouping, _workspace.ChartsEnabled);
        FollowLive = _workspace.FollowLive;
        ShowLegend = _workspace.ShowLegend;
        NotifyChartLayout();
        EnsureChoice(IntervalChoices, SampleIntervalMs, SampleIntervalMs + " ms");
        EnsureChoice(WindowChoices, WindowSeconds, WindowSeconds + " s");
        BindMachines();
        if (_currentCatalog.Sets.Count == 0)
            ShowCatalog(StarterCatalog.Create());
        else
            RebuildSets();
        RefreshPresentation();
    }

    private void ShowCatalog(CounterCatalog catalog)
    {
        _currentCatalog = catalog;
        CatalogBanner = catalog.Warning ?? (catalog.IsStarter
            ? "Starter list. Reload a machine for the full counter set."
            : catalog.Sets.Count + " counter sets from " + catalog.SourceHost);
        RebuildSets();
    }

    private void RebuildSets()
    {
        var previous = SelectedSet?.Name;
        var filtered = CatalogSearch.Filter(_currentCatalog, SearchText);
        CatalogSets.Clear();
        foreach (var set in filtered)
            CatalogSets.Add(set);
        SelectedSet = CatalogSets.FirstOrDefault(set => set.Name == previous) ?? CatalogSets.FirstOrDefault();
    }

    private void RebuildCounters()
    {
        var previous = SelectedCounter?.Name;
        Counters.Clear();
        if (SelectedSet is null)
        {
            RebuildInstances();
            return;
        }

        foreach (var counter in SelectedSet.Counters)
        {
            var row = new CounterRowViewModel(this) { Name = counter.Name, Help = counter.Help };
            row.SetChecked(WorkspaceEditor.HasCounter(_workspace, CurrentScopeId, SelectedSet.Name, counter.Name));
            Counters.Add(row);
        }

        SelectedCounter = Counters.FirstOrDefault(counter => counter.Name == previous) ?? Counters.FirstOrDefault();
    }

    private void RebuildInstances()
    {
        Instances.Clear();
        if (SelectedSet is null || SelectedCounter is null)
        {
            InstanceBanner = "";
            return;
        }

        InstanceBanner = SelectedSet.EnumerationError ?? "";
        var selected = WorkspaceEditor.InstancesFor(_workspace, CurrentScopeId, SelectedSet.Name, SelectedCounter.Name);
        var filter = InstanceFilter.Trim();
        if (string.IsNullOrEmpty(filter) && (SelectedSet.MultiInstance || SelectedSet.Instances.Count > 0))
            AddInstance("*", "*   every instance (except _Total)", selected);
        if (string.IsNullOrEmpty(filter) && !SelectedSet.MultiInstance && SelectedSet.Instances.Count == 0)
            AddInstance("", "(single instance)", selected);
        foreach (var instance in SelectedSet.Instances)
        {
            if (!InstanceMatches(instance, filter))
                continue;
            AddInstance(instance, instance, selected);
        }
        _ = ShowHelpAsync(SelectedSet, SelectedCounter);
    }

    public static bool InstanceMatches(string name, string filter) => TokenFilter.Matches(name, filter);


    private void AddInstance(string name, string label, IReadOnlyList<string> selected)
    {
        var row = new InstanceRowViewModel(this) { Name = name, Label = label };
        row.SetChecked(selected.Contains(name, StringComparer.OrdinalIgnoreCase));
        Instances.Add(row);
    }

    private async Task ShowHelpAsync(CounterSetInfo set, CounterRowViewModel counter)
    {
        if (!string.IsNullOrWhiteSpace(counter.Help))
        {
            HelpText = counter.Help;
            return;
        }

        var machine = SelectedTarget();
        if (machine is null || _currentCatalog.IsStarter)
        {
            HelpText = set.Help;
            return;
        }

        HelpText = "Loading explanation…";
        try
        {
            var text = await Task.Run(() => _catalog.ReadHelp(machine, set.Name, counter.Name)).ConfigureAwait(true);
            if (SelectedCounter == counter)
                HelpText = text;
        }
        catch (Exception ex)
        {
            if (SelectedCounter == counter)
                HelpText = ex.Message;
        }
    }

    private void BindMachines()
    {
        var selected = SelectedMachine?.Id;
        Machines.Clear();
        foreach (var machine in _workspace.Machines)
        {
            Machines.Add(new MachineItemViewModel(
                machine.Id,
                machine.Host,
                machine.DisplayName,
                machine.Credential.Mode == CredentialMode.Alternate,
                machine.Credential.UserName,
                machine.Cluster));
        }

        SelectedMachine = Machines.FirstOrDefault(machine => machine.Id == selected) ?? Machines.FirstOrDefault();
    }

    private MachineTarget? SelectedTarget() =>
        _workspace.Machines.FirstOrDefault(machine => machine.Id == SelectedMachine?.Id) ?? _workspace.Machines.FirstOrDefault();

    private void StopInternal()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }

        _engine.Stop();
        IsSampling = false;
    }

    private void PersistCredentials()
    {
        try
        {
            AppFiles.SaveCredentials(_vault, _protector);
        }
        catch (Exception ex)
        {
            Banner = "Could not store credentials. " + ex.Message;
        }
    }

    private static void EnsureChoice(ObservableCollection<Choice<int>> choices, int value, string label)
    {
        if (choices.All(choice => choice.Value != value))
            choices.Add(new Choice<int>(value, label));
    }

    private static string StatusLabel(MachineStatus status) => status switch
    {
        MachineStatus.Online => "Online",
        MachineStatus.Busy => "Reading",
        MachineStatus.Timeout => "Waiting",
        MachineStatus.Offline => "Offline",
        MachineStatus.AccessDenied => "Access denied",
        MachineStatus.Error => "Error",
        _ => "Idle"
    };

    private static byte[] ReadHeader(string path)
    {
        var buffer = new byte[64];
        using var stream = File.OpenRead(path);
        var read = stream.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }
}

public partial class MachineItemViewModel : ObservableObject
{
    public MachineItemViewModel(string id, string host, string displayName, bool useAlternate, string? userName, string? cluster = null)
    {
        Cluster = string.IsNullOrWhiteSpace(cluster) ? null : cluster.Trim();
        Id = id;
        Host = host;
        DisplayName = displayName;
        UseAlternate = useAlternate;
        UserName = userName ?? "";
    }

    public string Id { get; }
    public string Host { get; }
    public string DisplayName { get; }
    public bool UseAlternate { get; }
    public string UserName { get; }
    public string? Cluster { get; }
    public string HostLine => Cluster is null ? Host : Host + "  ·  cluster " + Cluster;
    public string CredentialText => UseAlternate ? (string.IsNullOrWhiteSpace(UserName) ? "Other user" : UserName) : "Current user";
    [ObservableProperty] private MachineStatus status = MachineStatus.Unknown;
    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private string detail = "";
}

public partial class ClusterNodeRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public ClusterNodeRowViewModel(MainViewModel owner, ClusterNodeCandidate candidate)
    {
        _owner = owner;
        Host = candidate.Host;
        ShortName = candidate.ShortName;
        State = candidate.State;
        AlreadyAdded = candidate.AlreadyAdded;
        isChecked = !candidate.AlreadyAdded;
    }

    public string Host { get; }
    public string ShortName { get; }
    public string State { get; }
    public bool AlreadyAdded { get; }
    public bool CanToggle => !AlreadyAdded;
    public string Note => AlreadyAdded ? "already added" : State;
    public bool IsDown => !AlreadyAdded && State is "Down" or "Unknown";
    [ObservableProperty] private bool isChecked;

    partial void OnIsCheckedChanged(bool value) => _owner.OnClusterNodeToggled();
}

public partial class CounterRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _suspend;

    public CounterRowViewModel(MainViewModel owner) => _owner = owner;
    public string Name { get; set; } = "";
    public string Help { get; set; } = "";
    [ObservableProperty] private bool isChecked;

    public void SetChecked(bool value)
    {
        _suspend = true;
        IsChecked = value;
        _suspend = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_suspend)
            _owner.OnCounterToggled(this, value);
    }
}

public partial class InstanceRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _suspend;

    public InstanceRowViewModel(MainViewModel owner) => _owner = owner;
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    [ObservableProperty] private bool isChecked;

    public void SetChecked(bool value)
    {
        _suspend = true;
        IsChecked = value;
        _suspend = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_suspend)
            _owner.OnInstanceToggled(this, value);
    }
}

public partial class SeriesRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _suspend;

    public SeriesRowViewModel(MainViewModel owner, string key)
    {
        _owner = owner;
        Key = key;
    }

    public string Key { get; }
    public string OverrideKey { get; private set; } = "";
    [ObservableProperty] private string color = "#4C9AFF";
    [ObservableProperty] private bool visible = true;
    [ObservableProperty] private string machine = "";
    [ObservableProperty] private string counter = "";
    [ObservableProperty] private string instance = "";
    [ObservableProperty] private string last = "—";
    [ObservableProperty] private string rowTip = "";
    [ObservableProperty] private string minimum = "—";
    [ObservableProperty] private string maximum = "—";
    [ObservableProperty] private string average = "—";
    [ObservableProperty] private double scale = 1;
    [ObservableProperty] private string scaleText = "1";
    [ObservableProperty] private bool isScaled;

    public void Apply(SeriesSnapshot snapshot)
    {
        _suspend = true;
        OverrideKey = snapshot.OverrideKey;
        Color = snapshot.Color;
        Visible = snapshot.Visible;
        Machine = snapshot.MachineDisplay;
        Counter = snapshot.Path.Category + "\\" + snapshot.Path.Counter;
        Instance = string.IsNullOrEmpty(snapshot.Path.Instance) ? "—" : snapshot.Path.Instance;
        Last = snapshot.Stats.Last is not null
            ? ValueFormat.Format(snapshot.Stats.Last)
            : (string.IsNullOrWhiteSpace(snapshot.LastError) ? "—" : snapshot.LastError);
        Minimum = ValueFormat.Format(snapshot.Stats.Min);
        Maximum = ValueFormat.Format(snapshot.Stats.Max);
        Average = ValueFormat.Format(snapshot.Stats.Average);
        Scale = snapshot.Scale;
        ScaleText = ScaleFormat.Format(snapshot.Scale);
        IsScaled = !ScaleFormat.IsIdentity(snapshot.Scale);
        RowTip = string.IsNullOrWhiteSpace(snapshot.LastError) ? "" : snapshot.LastError;
        _suspend = false;
    }

    partial void OnScaleChanged(double value)
    {
        if (!_suspend && !string.IsNullOrEmpty(OverrideKey))
            _owner.SetScale(OverrideKey, value);
    }

    partial void OnScaleTextChanged(string value)
    {
        if (_suspend || string.IsNullOrEmpty(OverrideKey))
            return;
        if (ScaleFormat.TryParse(value, out var parsed))
            _owner.SetScale(OverrideKey, parsed);
    }

    partial void OnVisibleChanged(bool value)
    {
        if (!_suspend && !string.IsNullOrEmpty(OverrideKey))
            _owner.SetVisible(OverrideKey, value);
    }
}

public partial class ChartViewModel : ObservableObject
{
    public ChartViewModel(string key) => Key = key;
    public string Key { get; }
    [ObservableProperty] private string title = "";
    [ObservableProperty] private ChartFrame? frame;

    /// <summary>Height set by dragging the bottom edge; null means the default height.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(IsResized))]
    private double? customHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    private double defaultHeight = ChartSizing.DefaultMultiHeight;

    public double DisplayHeight => CustomHeight ?? DefaultHeight;
    public bool IsResized => CustomHeight is not null;
}
