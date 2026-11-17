using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PerfMonNext.Core;
using ScottPlot;
using ScottPlot.Panels;

namespace PerfMonNext.Views;

public partial class LiveChartView : UserControl
{
    private bool _drawn;
    private double _lastWidth;
    private IPanel? _legendPanel;
    private Pixel? _hoverPixel;
    private System.Windows.Point _hoverPoint;

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
        // No "Rendered in … (FPS)" benchmark on double-click.
        Plot.UserInputProcessor.DoubleLeftClickBenchmark(false);
        Plot.PreviewMouseMove += (_, e) =>
        {
            _hoverPixel = Plot.GetPlotPixelPosition(e);
            _hoverPoint = e.GetPosition(this);
            UpdateHover();
        };
        Plot.MouseLeave += (_, _) =>
        {
            _hoverPixel = null;
            HoverLayer.Visibility = Visibility.Collapsed;
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

        plot.Benchmark.IsVisible = false;
        var fill = Frame.Series.Count <= 4;
        foreach (var series in Frame.Series)
        {
            if (series.Xs.Length == 0)
                continue;
            var scatter = plot.Add.Scatter(series.Xs, series.Ys);
            scatter.MarkerSize = 0;
            scatter.LineWidth = 1.8f;
            var color = ScottPlot.Color.FromHex(series.Color);
            scatter.Color = color;
            if (fill && !series.Ys.Any(double.IsNaN) && series.Ys.All(y => y >= 0))
            {
                scatter.FillY = true;
                scatter.FillYValue = 0;
                scatter.FillYColor = color.WithAlpha(0.12);
            }
            scatter.LegendText = series.Legend.Length == 0 || series.LegendDetail.Length == 0
                ? series.Legend
                : series.Legend + "   " + series.LegendDetail;
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
        UpdateHover();
    }

    private void UpdateHover()
    {
        if (_hoverPixel is not Pixel pixel || Frame is null || !_drawn)
        {
            HoverLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var plot = Plot.Plot;
        var data = plot.LastRender.DataRect;
        var figure = plot.LastRender.FigureRect;
        if (figure.Width <= 0 || pixel.X < data.Left || pixel.X > data.Right || pixel.Y < data.Top || pixel.Y > data.Bottom)
        {
            HoverLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var x = plot.GetCoordinates(pixel).X;
        var info = ChartHover.At(Frame, x);
        if (info is null || info.Rows.Count == 0)
        {
            HoverLayer.Visibility = Visibility.Collapsed;
            return;
        }

        // Plot pixels can differ from WPF units (DPI); convert through the figure size.
        var ratio = Plot.ActualHeight / figure.Height;
        Crosshair.X1 = Crosshair.X2 = _hoverPoint.X;
        Crosshair.Y1 = data.Top * ratio;
        Crosshair.Y2 = data.Bottom * ratio;

        HoverTime.Text = info.Time.ToString("yyyy-MM-dd HH:mm:ss");
        HoverRows.Children.Clear();
        foreach (var row in info.Rows)
            HoverRows.Children.Add(BuildRow(row));
        if (info.Hidden > 0)
            HoverRows.Children.Add(new TextBlock { Text = "+" + info.Hidden + " more", Foreground = Brush("#8B9CB0"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        HoverLayer.Visibility = Visibility.Visible;
        HoverCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HoverCard.DesiredSize;
        var left = _hoverPoint.X + 14;
        if (left + size.Width > ActualWidth - 4)
            left = _hoverPoint.X - 14 - size.Width;
        var top = Math.Clamp(_hoverPoint.Y - size.Height / 2, 2, Math.Max(2, ActualHeight - size.Height - 2));
        Canvas.SetLeft(HoverCard, Math.Max(2, left));
        Canvas.SetTop(HoverCard, top);
    }

    private static FrameworkElement BuildRow(HoverRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var swatch = new Border { Width = 12, Height = 3, Background = Brush(row.Color), VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBlock { Text = row.Label, Foreground = Brush("#E8EEF5"), FontSize = 12, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
        var value = new TextBlock { Text = row.Text, Foreground = Brush("#FFFFFF"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }

    private static SolidColorBrush Brush(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
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
        // Grafana-like: no box, only a faint baseline and left edge; compact K/M/G ticks.
        var grid = ScottPlot.Color.FromHex(palette.Grid);
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Left.FrameLineStyle.Color = grid;
        plot.Axes.Bottom.FrameLineStyle.Color = grid;
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = CompactFormat.Format };
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(palette.Grid);
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex(palette.Background);
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex(palette.LegendBackground);
        plot.Legend.FontColor = ScottPlot.Color.FromHex(palette.LegendText);
        plot.Legend.FontSize = 10;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex(palette.LegendOutline);
        plot.Legend.ShadowColor = ScottPlot.Color.FromHex("#00000000");
    }
}
