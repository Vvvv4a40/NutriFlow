using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui.Tests;

public sealed partial class ThemePaletteTests
{
    public static TheoryData<string> Themes => new(ThemePalette.All.Select(theme => theme.Id));

    [Fact]
    public void EightThemesWithTheDefaultAndFallback()
    {
        Assert.Equal(["indigo", "orange", "sand", "terracotta", "mandarin", "taiga", "dark", "light"],
            ThemePalette.All.Select(theme => theme.Id));
        Assert.Equal("terracotta", ThemePalette.Find(null).Id);
        Assert.Equal("terracotta", ThemePalette.Find("unknown").Id);
        Assert.Equal("taiga", ThemePalette.Find("taiga").Id);
    }

    // the pairs the android palettes were checked with: text 4.5:1, field outlines 3:1
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextKeepsWcagContrastOnItsSurfaces(string id)
    {
        ThemePalette theme = ThemePalette.Find(id);
        foreach (string surface in new[] { theme.Background, theme.Surface })
        {
            AssertContrast(theme.TextPrimary, surface, 4.5, id);
            AssertContrast(theme.Muted, surface, 4.5, id);
            AssertContrast(theme.Highlight, surface, 4.5, id);
            AssertContrast(theme.Accent, surface, 4.5, id);
            AssertContrast(theme.Success, surface, 4.5, id);
            AssertContrast(theme.Warning, surface, 4.5, id);
            AssertContrast(theme.Danger, surface, 4.5, id);
        }

        AssertContrast(theme.TextPrimary, theme.SurfaceRaised, 4.5, id);
        AssertContrast(theme.OnAccent, theme.AccentFill, 4.5, id);
        AssertContrast(theme.DateForeground, theme.DateBackground, 4.5, id);
        AssertContrast(theme.FieldStroke, theme.SurfaceRaised, 3, id);
    }

    [Fact]
    public void CssDefaultsAreTheDefaultTheme()
    {
        string css = File.ReadAllText(Path.Combine(SourceDirectory(), "..", "NutriFlow.Ui", "wwwroot", "nutriflow-ui.css"));
        Dictionary<string, string> defaults = CssVariable().Matches(css)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);

        foreach ((string name, string value) in ThemePalette.Find(ThemePalette.DefaultId).CssVariables())
        {
            Assert.True(defaults.TryGetValue(name, out string? css_value), $"{name} is missing in nutriflow-ui.css");
            Assert.Equal(value, css_value, ignoreCase: true);
        }
    }

    private static void AssertContrast(string foreground, string background, double minimum, string theme)
    {
        double ratio = Contrast(foreground, background);
        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    private static double Contrast(string first, string second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string value)
        {
            double channel = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(hex[1..3]) + 0.7152 * Channel(hex[3..5]) + 0.0722 * Channel(hex[5..7]);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [GeneratedRegex(@"(--[a-z-]+):\s*(#[0-9A-Fa-f]{6});")]
    private static partial Regex CssVariable();
}
