using System.Globalization;

namespace PerfMonNext.Core;

/// <summary>Colors for chart axes, grid, and legend, one set per UI skin.</summary>
public sealed record ChartPalette(
    string Name,
    string Background,
    string TickLabel,
    string AxisTitle,
    string AxisLine,
    string MajorTick,
    string MinorTick,
    string Grid,
    string LegendBackground,
    string LegendText,
    string LegendOutline,
    string OverflowText)
{
    public static ChartPalette Dark { get; } = new(
        Name: "Dark",
        Background: "#10161D",
        TickLabel: "#E8EEF5",
        AxisTitle: "#E8EEF5",
        AxisLine: "#8B9CB0",
        MajorTick: "#C5D0DC",
        MinorTick: "#5A6B7E",
        Grid: "#2A3646",
        LegendBackground: "#18212B",
        LegendText: "#E8EEF5",
        LegendOutline: "#3A4A5C",
        OverflowText: "#C5D0DC");

    public static ChartPalette Light { get; } = new(
        Name: "Light",
        Background: "#FFFFFF",
        TickLabel: "#1F2933",
        AxisTitle: "#1F2933",
        AxisLine: "#52606D",
        MajorTick: "#3E4C59",
        MinorTick: "#9AA5B1",
        Grid: "#E4E7EB",
        LegendBackground: "#F5F7FA",
        LegendText: "#1F2933",
        LegendOutline: "#CBD2D9",
        OverflowText: "#3E4C59");

    /// <summary>Palette for a skin name; unknown or empty names fall back to Dark (the shipped skin).</summary>
    public static ChartPalette ForSkin(string? skin) =>
        string.Equals(skin, "Light", StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    /// <summary>WCAG 2.x contrast ratio between two #RRGGBB colors (1..21).</summary>
    public static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 8)
            text = text[2..];
        if (text.Length != 6)
            throw new FormatException("Expected #RRGGBB: " + hex);
        double Channel(int offset)
        {
            var c = int.Parse(text.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
