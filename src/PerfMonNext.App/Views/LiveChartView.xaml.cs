using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerfMonNext.Core;
using ScottPlot;
using ScottPlot.Panels;

namespace PerfMonNext.Views;

public partial class LiveChartView : UserControl
{
    private bool _drawn;
    private double _lastWidth;
    private IPanel? _legendPanel;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame),
        typeof(ChartFrame),
        typeof(LiveChartView),
        new PropertyMetadata(null, (d, _) => ((LiveChartView)d).Redraw()));

    public static readonly DependencyProperty FollowLiveProperty = DependencyProperty.Register(
        nameof(FollowLive),
        typeof(bool),
        typeof(LiveChartView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) =>
        {
            if (e.NewValue is true)
                ((LiveChartView)d).Redraw();
        }));

    public LiveChartView()
    {
        InitializeComponent();
        Plot.PreviewMouseWheel += (_, _) => FollowLive = false;
        Plot.PreviewMouseRightButtonDown += (_, _) => FollowLive = false;
        Plot.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
                FollowLive = false;
        };
        Plot.SizeChanged += (_, _) =>
        {
            if (Math.Abs(Plot.ActualWidth - _lastWidth) < 16)
                return;
            _lastWidth = Plot.ActualWidth;
            Redraw();
        };
        Loaded += (_, _) => Redraw();
    }

    public ChartFrame? Frame
    {
        get => (ChartFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public bool FollowLive
    {
        get => (bool)GetValue(FollowLiveProperty);
        set => SetValue(FollowLiveProperty, value);
    }

    private void Redraw()
    {
        if (!IsLoaded || Frame is null)
            return;

        var plot = Plot.Plot;
        AxisLimits? keep = null;
        if (!FollowLive && _drawn)
            keep = plot.Axes.GetLimits();

        if (_legendPanel is not null)
        {
            plot.Axes.Remove(_legendPanel);
            _legendPanel = null;
        }

        plot.Clear();
        plot.HideLegend();
        plot.Legend.IsVisible = false;

        var palette = ChartPalette.ForSkin(TryFindResource("App.ChartSkin") as string);
        plot.FigureBackground.Color = ScottPlot.Color.FromHex(palette.Background);
        plot.DataBackground.Color = ScottPlot.Color.FromHex(palette.Background);

        foreach (var series in Frame.Series)
        {
            if (series.Xs.Length == 0)
                continue;
            var scatter = plot.Add.Scatter(series.Xs, series.Ys);
            scatter.MarkerSize = 0;
            scatter.LineWidth = 1.6f;
            scatter.Color = ScottPlot.Color.FromHex(series.Color);
            scatter.LegendText = series.Legend;
        }

        if (Frame.ShowLegend && Frame.LegendOverflow > 0)
        {
            var marker = plot.Add.Scatter(new double[] { double.NaN }, new double[] { double.NaN });
            marker.LegendText = "+" + Frame.LegendOverflow + " more";
            marker.MarkerSize = 0;
            marker.LineWidth = 0;
            marker.Color = ScottPlot.Color.FromHex(palette.OverflowText);
        }

        var bottom = plot.Axes.DateTimeTicksBottom();
        if (bottom.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
        {
            ticks.LabelFormatter = value => value.ToString("HH:mm:ss");
            ticks.MaxTickCount = ChartTicks.CountForWidth(Plot.ActualWidth);
        }
        plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plot.Axes.Bottom.MinimumSize = ChartTicks.BottomPad;

        // Style after DateTimeTicksBottom(): it replaces the bottom axis with a fresh one that
        // carries ScottPlot's default (dark) colors, which made the X labels unreadable.
        ApplyPalette(plot, palette);

        if (Frame.ShowLegend)
        {
            plot.Legend.IsVisible = true;
            _legendPanel = plot.ShowLegend(Edge.Right);
        }
        else
        {
            plot.HideLegend();
            plot.Legend.IsVisible = false;
        }

        if (FollowLive && Frame.XMin is double x1 && Frame.XMax is double x2 && Frame.YMin is double y1 && Frame.YMax is double y2)
            plot.Axes.SetLimits(x1, x2, y1, y2);
        else if (keep is { } saved)
            plot.Axes.SetLimits(saved.Left, saved.Right, saved.Bottom, saved.Top);
        else if (Frame.XMin is double fx1 && Frame.XMax is double fx2 && Frame.YMin is double fy1 && Frame.YMax is double fy2)
            plot.Axes.SetLimits(fx1, fx2, fy1, fy2);

        Plot.Refresh();
        _drawn = true;
        _lastWidth = Plot.ActualWidth;
    }

    private static void ApplyPalette(ScottPlot.Plot plot, ChartPalette palette)
    {
        var tick = ScottPlot.Color.FromHex(palette.TickLabel);
        var title = ScottPlot.Color.FromHex(palette.AxisTitle);
        var line = ScottPlot.Color.FromHex(palette.AxisLine);
        var major = ScottPlot.Color.FromHex(palette.MajorTick);
        var minor = ScottPlot.Color.FromHex(palette.MinorTick);
        foreach (var axis in plot.Axes.GetAxes())
        {
            axis.TickLabelStyle.ForeColor = tick;
            axis.Label.ForeColor = title;
            axis.FrameLineStyle.Color = line;
            axis.MajorTickStyle.Color = major;
            axis.MinorTickStyle.Color = minor;
        }

        plot.Axes.Bottom.TickLabelStyle.FontSize = ChartTicks.FontSize;
        plot.Axes.Left.TickLabelStyle.FontSize = 12;
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(palette.Grid);
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex(palette.Background);
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex(palette.LegendBackground);
        plot.Legend.FontColor = ScottPlot.Color.FromHex(palette.LegendText);
        plot.Legend.FontSize = 10;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex(palette.LegendOutline);
        plot.Legend.ShadowColor = ScottPlot.Color.FromHex("#00000000");
    }
}
