using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerfMonNext.Core;
using ScottPlot;

namespace PerfMonNext.Views;

public partial class LiveChartView : UserControl
{
    private bool _drawn;

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

        plot.Clear();
        var tick = ScottPlot.Color.FromHex("#E8EEF5");
        var muted = ScottPlot.Color.FromHex("#C5D0DC");
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#10161D");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#10161D");
        plot.Axes.Color(tick);
        plot.Axes.Bottom.TickLabelStyle.ForeColor = tick;
        plot.Axes.Left.TickLabelStyle.ForeColor = tick;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        plot.Axes.Bottom.MajorTickStyle.Color = muted;
        plot.Axes.Left.MajorTickStyle.Color = muted;
        plot.Axes.Bottom.MinorTickStyle.Color = ScottPlot.Color.FromHex("#3A4A5C");
        plot.Axes.Left.MinorTickStyle.Color = ScottPlot.Color.FromHex("#3A4A5C");
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2A3646");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#18212B");
        plot.Legend.FontColor = tick;
        plot.Legend.FontSize = 12;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#2A3646");

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

        var bottom = plot.Axes.DateTimeTicksBottom();
        if (bottom.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
        {
            ticks.LabelFormatter = value => value.ToString("HH:mm:ss");
            ticks.MaxTickCount = 6;
        }
        plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plot.Axes.Bottom.MinimumSize = 28;
        if (Frame.ShowLegend)
            plot.ShowLegend(Alignment.UpperLeft);

        if (FollowLive && Frame.XMin is double x1 && Frame.XMax is double x2 && Frame.YMin is double y1 && Frame.YMax is double y2)
            plot.Axes.SetLimits(x1, x2, y1, y2);
        else if (keep is { } saved)
            plot.Axes.SetLimits(saved.Left, saved.Right, saved.Bottom, saved.Top);
        else if (Frame.XMin is double fx1 && Frame.XMax is double fx2 && Frame.YMin is double fy1 && Frame.YMax is double fy2)
            plot.Axes.SetLimits(fx1, fx2, fy1, fy2);

        Plot.Refresh();
        _drawn = true;
    }
}
