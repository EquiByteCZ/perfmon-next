namespace PerfMonNext.Core;

/// <summary>Height rules for chart panels the user resizes by dragging their bottom edge.</summary>
public static class ChartSizing
{
    public const double MinHeight = 140;
    public const double MaxHeight = 2400;
    public const double DefaultMultiHeight = 260;
    public const double DefaultSingleMinHeight = 220;

    /// <summary>Default panel height: one chart fills the viewport, several charts get a fixed height and scroll.</summary>
    public static double Default(int chartCount, double viewportHeight) =>
        chartCount <= 1 ? Math.Max(DefaultSingleMinHeight, viewportHeight) : DefaultMultiHeight;

    public static double Clamp(double height) =>
        double.IsNaN(height) ? MinHeight : Math.Clamp(height, MinHeight, MaxHeight);

    /// <summary>Height after a drag of <paramref name="delta"/> pixels starting from <paramref name="current"/>.</summary>
    public static double Drag(double current, double delta) => Math.Round(Clamp(current + delta));
}
