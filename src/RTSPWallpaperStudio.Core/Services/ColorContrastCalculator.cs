namespace RTSPWallpaperStudio.Core.Services;

public static class ColorContrastCalculator
{
    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        var foreground = RelativeLuminance(Parse(foregroundHex));
        var background = RelativeLuminance(Parse(backgroundHex));
        var lighter = Math.Max(foreground, background);
        var darker = Math.Min(foreground, background);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static (double R, double G, double B) Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6)
        {
            throw new ArgumentException("#RRGGBB形式で指定してください。", nameof(hex));
        }

        return (Convert.ToInt32(value[..2], 16) / 255d,
            Convert.ToInt32(value[2..4], 16) / 255d,
            Convert.ToInt32(value[4..6], 16) / 255d);
    }

    private static double RelativeLuminance((double R, double G, double B) color)
    {
        static double Linearize(double channel) => channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }
}
