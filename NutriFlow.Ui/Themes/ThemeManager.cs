using Microsoft.JSInterop;

namespace NutriFlow.Ui.Themes;

// the palette lives in C#, the page gets it as css custom properties; setting them through the cssom stays within a csp
// that forbids inline styles, and the browser bar follows the background through the theme-color meta tag
public sealed class ThemeManager(IJSRuntime js)
{
    private Task<IJSObjectReference>? _module;

    public ThemePalette Current { get; private set; } = ThemePalette.Find(ThemePalette.DefaultId);

    public event Action? Changed;

    public async Task ApplyAsync(string? id)
    {
        ThemePalette theme = ThemePalette.Find(id);
        await (await GetModuleAsync()).InvokeVoidAsync("applyTheme", theme.Id, theme.CssVariables(), theme.Background,
            theme.NavBackground == theme.Background, theme.IsDark);
        if (theme != Current)
        {
            Current = theme;
            Changed?.Invoke();
        }
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/NutriFlow.Ui/theme.mjs").AsTask();
}
