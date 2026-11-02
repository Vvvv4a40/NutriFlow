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

    // the same split as Theme.IsDark on android: native controls of these themes get the dark colour scheme
    [Fact]
    public void DarkThemesAreTheThreeWithADarkBackground() =>
        Assert.Equal(["indigo", "taiga", "dark"], ThemePalette.All.Where(theme => theme.IsDark).Select(theme => theme.Id));

    // the eaten and goal tiles of the diary: SurfaceRaised at 55 % over the panel (.nf-stat), as on android
    [Theory]
    [MemberData(nameof(Themes))]
    public void DiaryTileTextKeepsWcagContrast(string id)
    {
        ThemePalette theme = ThemePalette.Find(id);
        string tile = Blend(theme.SurfaceRaised, theme.Surface, 0.55);

        AssertContrast(theme.TextPrimary, tile, 4.5, id);
        AssertContrast(theme.Muted, tile, 4.5, id);
        AssertContrast(theme.Highlight, tile, 4.5, id);
        AssertContrast(theme.Accent, tile, 4.5, id);
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

    // color-mix in srgb: each channel weighted, then rounded like the browser does
    private static string Blend(string top, string bottom, double share)
    {
        static int Channel(string hex, int start) => int.Parse(hex[start..(start + 2)], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int Mix(int start) => (int)Math.Round(Channel(top, start) * share + Channel(bottom, start) * (1 - share));
        return $"#{Mix(1):X2}{Mix(3):X2}{Mix(5):X2}";
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
