namespace NutriFlow.Ui.Themes;

// the eight themes of the android app (mobile/NutriFlow.Mobile/Theme.cs, approved by the founder on 30.10.2026);
// every theme has one fixed look, the second colour of the pair carries the navigation bar, the date and the headings
public sealed class ThemePalette(string id, string name, string swatchBase, string swatchAccent)
{
    public const string DefaultId = "terracotta";

    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        new("indigo", "Индиго и янтарь", "#3D348B", "#F7B801")
        {
            Background = "#2F2873", Surface = "#3D348B", SurfaceRaised = "#4B42A3", SurfacePressed = "#564D97", Stroke = "#504894",
            TextPrimary = "#FFF8E8", Muted = "#D2CCF2", Subtle = "#C6C0EA", Highlight = "#F7B801",
            Accent = "#F7B801", AccentFill = "#F7B801", AccentPressed = "#D59F0F", AccentSoft = "#594876", OnAccent = "#231D57", FieldStroke = "#A19AD6",
            Protein = "#8FD3F4", Fat = "#FF9E7A", Carbs = "#F5A8D8", Success = "#95D5AE", Warning = "#F1BD6E", Danger = "#F5B9C4",
            NavBackground = "#F7B801", NavForeground = "#4A3F8C", NavPressed = "#EBB00B", NavSelected = "#2F2873", NavSelectedIcon = "#F7B801", NavSelectedText = "#231D57",
            FabBackground = "#2F2873", FabForeground = "#F7B801", FabPressed = "#4F3F61", DateBackground = "#F7B801", DateForeground = "#231D57", DatePressed = "#D9A20D"
        },
        new("orange", "Оранж и бирюза", "#F4A261", "#2A9D8F")
        {
            Background = "#F4A261", Surface = "#F8B983", SurfaceRaised = "#FBD0A8", SurfacePressed = "#DDA372", Stroke = "#E3A876",
            TextPrimary = "#261204", Muted = "#5A3110", Subtle = "#653B18", Highlight = "#0B4A43",
            Accent = "#0B4A43", AccentFill = "#2A9D8F", AccentPressed = "#258C7F", AccentSoft = "#CBB386", OnAccent = "#021714", FieldStroke = "#7F5633",
            Protein = "#1D4E89", Fat = "#5E3A00", Carbs = "#0F6B5E", Success = "#1C4C31", Warning = "#653C00", Danger = "#7F1E2F",
            NavBackground = "#2A9D8F", NavForeground = "#04221E", NavPressed = "#279487", NavSelected = "#F4A261", NavSelectedIcon = "#261204", NavSelectedText = "#021714",
            FabBackground = "#F4A261", FabForeground = "#0B4A43", FabPressed = "#CF945C", DateBackground = "#2A9D8F", DateForeground = "#021714", DatePressed = "#258C7F"
        },
        new("sand", "Песок и графит", "#E9C46A", "#264653")
        {
            Background = "#E9C46A", Surface = "#F2D58E", SurfaceRaised = "#F8E5B5", SurfacePressed = "#D2BC81", Stroke = "#DCC485",
            TextPrimary = "#1A2F38", Muted = "#3A505A", Subtle = "#41555C", Highlight = "#264653",
            Accent = "#264653", AccentFill = "#264653", AccentPressed = "#476064", AccentSoft = "#C5B681", OnAccent = "#F6E7BF", FieldStroke = "#626F6D",
            Protein = "#2A6F97", Fat = "#9A4A12", Carbs = "#1C7468", Success = "#225A39", Warning = "#744500", Danger = "#922335",
            NavBackground = "#264653", NavForeground = "#E9D7A6", NavPressed = "#455D60", NavSelected = "#E9C46A", NavSelectedIcon = "#264653", NavSelectedText = "#F6E7BF",
            FabBackground = "#E9C46A", FabForeground = "#264653", FabPressed = "#CAB066", DateBackground = "#264653", DateForeground = "#F6E7BF", DatePressed = "#435D62"
        },
        new("terracotta", "Терракота и лён", "#EDE2D1", "#A2461F")
        {
            Background = "#EDE2D1", Surface = "#F6EEE2", SurfaceRaised = "#E3D5C0", SurfacePressed = "#D4CBC0", Stroke = "#E2DACE",
            TextPrimary = "#2D211B", Muted = "#65574B", Subtle = "#685A4E", Highlight = "#963F1B",
            Accent = "#963F1B", AccentFill = "#A2461F", AccentPressed = "#AF603E", AccentSoft = "#E5CCBB", OnAccent = "#FFFFFF", FieldStroke = "#827466",
            Protein = "#3F6286", Fat = "#946C1C", Carbs = "#6E5A8E", Success = "#276842", Warning = "#865000", Danger = "#A8283E",
            NavBackground = "#A2461F", NavForeground = "#F6E3D3", NavPressed = "#A64E28", NavSelected = "#EDE2D1", NavSelectedIcon = "#963F1B", NavSelectedText = "#FFF7EE",
            FabBackground = "#EDE2D1", FabForeground = "#963F1B", FabPressed = "#DFC8B4", DateBackground = "#A2461F", DateForeground = "#FFF7EE", DatePressed = "#AD5B38"
        },
        new("mandarin", "Мандарин и слива", "#FB8B24", "#5F0F40")
        {
            Background = "#FB8B24", Surface = "#FDA650", SurfaceRaised = "#FFC487", SurfacePressed = "#E29149", Stroke = "#E8964A",
            TextPrimary = "#2A0518", Muted = "#4A1A33", Subtle = "#5F2B36", Highlight = "#5F0F40",
            Accent = "#5F0F40", AccentFill = "#5F0F40", AccentPressed = "#79355F", AccentSoft = "#DA854C", OnAccent = "#FFFFFF", FieldStroke = "#794649",
            Protein = "#1F4E8C", Fat = "#6B4300", Carbs = "#0F5E46", Success = "#19422B", Warning = "#583400", Danger = "#721B2A",
            NavBackground = "#5F0F40", NavForeground = "#F6C9A8", NavPressed = "#772D51", NavSelected = "#FB8B24", NavSelectedIcon = "#5F0F40", NavSelectedText = "#FFD9B8",
            FabBackground = "#FB8B24", FabForeground = "#5F0F40", FabPressed = "#E27728", DateBackground = "#5F0F40", DateForeground = "#FFE7D1", DatePressed = "#752D54"
        },
        new("taiga", "Тайга", "#17352A", "#F2A33A")
        {
            Background = "#17352A", Surface = "#1F4637", SurfaceRaised = "#2A5644", SurfacePressed = "#385B4D", Stroke = "#345849",
            TextPrimary = "#F1F5EC", Muted = "#B9CDBF", Subtle = "#B1C6B8", Highlight = "#F7B95E",
            Accent = "#F7B95E", AccentFill = "#F2A33A", AccentPressed = "#D28D31", AccentSoft = "#455738", OnAccent = "#2A1700", FieldStroke = "#88A595",
            Protein = "#9CCFE6", Fat = "#E3E08A", Carbs = "#F48FA0", Success = "#92D4AB", Warning = "#F0BD6E", Danger = "#F5B2BD",
            NavBackground = "#F2A33A", NavForeground = "#2C4A37", NavPressed = "#ECA03A", NavSelected = "#17352A", NavSelectedIcon = "#F2A33A", NavSelectedText = "#102A20",
            FabBackground = "#17352A", FabForeground = "#F2A33A", FabPressed = "#3A472D", DateBackground = "#F2A33A", DateForeground = "#102A20", DatePressed = "#D29236"
        },
        new("dark", "Тёмная", "#0F0F10", "#F3F3F3")
        {
            Background = "#0F0F10", Surface = "#19191B", SurfaceRaised = "#252528", SurfacePressed = "#2F2F31", Stroke = "#2F2F31",
            TextPrimary = "#F3F3F3", Muted = "#A3A3A8", Subtle = "#8D8D91", Highlight = "#F3F3F3",
            Accent = "#F3F3F3", AccentFill = "#F3F3F3", AccentPressed = "#CFCFCF", AccentSoft = "#49494B", OnAccent = "#0F0F10", FieldStroke = "#717175",
            Protein = "#8DB3E2", Fat = "#E2C17A", Carbs = "#C9A6DA", Success = "#8FD3A8", Warning = "#F0B65E", Danger = "#F0899A",
            NavBackground = "#0F0F10", NavForeground = "#A3A3A8", NavPressed = "#272728", NavSelected = "#2A2A2D", NavSelectedIcon = "#F3F3F3", NavSelectedText = "#F3F3F3",
            FabBackground = "#F3F3F3", FabForeground = "#0F0F10", FabPressed = "#CFCFCF", DateBackground = "#19191B", DateForeground = "#F3F3F3", DatePressed = "#383839"
        },
        new("light", "Светлая", "#FFFFFF", "#111113")
        {
            Background = "#F5F5F6", Surface = "#FFFFFF", SurfaceRaised = "#ECECEE", SurfacePressed = "#E0E0E0", Stroke = "#E7E7E7",
            TextPrimary = "#111113", Muted = "#5E5E66", Subtle = "#696971", Highlight = "#111113",
            Accent = "#111113", AccentFill = "#111113", AccentPressed = "#373739", AccentSoft = "#CBCBCB", OnAccent = "#FFFFFF", FieldStroke = "#84848B",
            Protein = "#2F6FB0", Fat = "#946500", Carbs = "#8A4F9E", Success = "#2C724A", Warning = "#8A5200", Danger = "#A8283E",
            NavBackground = "#F5F5F6", NavForeground = "#5E5E66", NavPressed = "#DDDDDF", NavSelected = "#E4E4E7", NavSelectedIcon = "#111113", NavSelectedText = "#111113",
            FabBackground = "#111113", FabForeground = "#FFFFFF", FabPressed = "#373739", DateBackground = "#FFFFFF", DateForeground = "#111113", DatePressed = "#DEDEDE"
        }
    ];

    public static ThemePalette Find(string? id) =>
        All.FirstOrDefault(theme => theme.Id == id) ?? All.First(theme => theme.Id == DefaultId);

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string SwatchBase { get; } = swatchBase;
    public string SwatchAccent { get; } = swatchAccent;
    public required string Background { get; init; }
    public required string Surface { get; init; }
    public required string SurfaceRaised { get; init; }
    public required string SurfacePressed { get; init; }
    public required string Stroke { get; init; }
    public required string TextPrimary { get; init; }
    public required string Muted { get; init; }
    public required string Subtle { get; init; }
    public required string Highlight { get; init; }
    public required string Accent { get; init; }
    public required string AccentFill { get; init; }
    public required string AccentPressed { get; init; }
    public required string AccentSoft { get; init; }
    public required string OnAccent { get; init; }
    public required string FieldStroke { get; init; }
    public required string Protein { get; init; }
    public required string Fat { get; init; }
    public required string Carbs { get; init; }
    public required string Success { get; init; }
    public required string Warning { get; init; }
    public required string Danger { get; init; }
    public required string NavBackground { get; init; }
    public required string NavForeground { get; init; }
    public required string NavPressed { get; init; }
    public required string NavSelected { get; init; }
    public required string NavSelectedIcon { get; init; }
    public required string NavSelectedText { get; init; }
    public required string FabBackground { get; init; }
    public required string FabForeground { get; init; }
    public required string FabPressed { get; init; }
    public required string DateBackground { get; init; }
    public required string DateForeground { get; init; }
    public required string DatePressed { get; init; }

    // css custom properties of the theme, listed by hand because the trimmer removes what reflection would need
    public IReadOnlyDictionary<string, string> CssVariables() => new Dictionary<string, string>
    {
        ["--background"] = Background,
        ["--surface"] = Surface,
        ["--surface-raised"] = SurfaceRaised,
        ["--surface-pressed"] = SurfacePressed,
        ["--stroke"] = Stroke,
        ["--text-primary"] = TextPrimary,
        ["--muted"] = Muted,
        ["--subtle"] = Subtle,
        ["--highlight"] = Highlight,
        ["--accent"] = Accent,
        ["--accent-fill"] = AccentFill,
        ["--accent-pressed"] = AccentPressed,
        ["--accent-soft"] = AccentSoft,
        ["--on-accent"] = OnAccent,
        ["--field-stroke"] = FieldStroke,
        ["--protein"] = Protein,
        ["--fat"] = Fat,
        ["--carbs"] = Carbs,
        ["--success"] = Success,
        ["--warning"] = Warning,
        ["--danger"] = Danger,
        ["--nav-background"] = NavBackground,
        ["--nav-foreground"] = NavForeground,
        ["--nav-pressed"] = NavPressed,
        ["--nav-selected"] = NavSelected,
        ["--nav-selected-icon"] = NavSelectedIcon,
        ["--nav-selected-text"] = NavSelectedText,
        ["--fab-background"] = FabBackground,
        ["--fab-foreground"] = FabForeground,
        ["--fab-pressed"] = FabPressed,
        ["--date-background"] = DateBackground,
        ["--date-foreground"] = DateForeground,
        ["--date-pressed"] = DatePressed
    };
}
