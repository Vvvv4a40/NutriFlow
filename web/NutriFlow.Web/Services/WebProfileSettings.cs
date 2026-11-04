using Microsoft.JSInterop;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Services;

// the browser counterpart of ProfileSettings in NutriFlow.Mobile: same keys and defaults, kept in localStorage.
// The Groq key is stored in plain localStorage too; the strict csp without third-party scripts is its protection.
public sealed class WebProfileSettings(IJSRuntime js) : IProfileSettings
{
    public const string DefaultTheme = "terracotta";
    private static readonly GroqSettings Defaults = new("default");

    public async Task<string> GetThemeAsync(Guid profileId) =>
        await GetAsync(Key(profileId, "theme")) ?? DefaultTheme;

    public Task SaveThemeAsync(Guid profileId, string theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        return SetAsync(Key(profileId, "theme"), theme);
    }

    public async Task<GroqSettings> GetGroqAsync(Guid profileId)
    {
        string? apiKey = await GetAsync(Key(profileId, "groq-key"));
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Добавьте личный ключ Groq в разделе «Настройки». Без ключа можно записать продукт из каталога или своё блюдо.");
        }

        return new GroqSettings(apiKey, await GetTextModelAsync(profileId), await GetVisionModelAsync(profileId),
            await GetSpeechModelAsync(profileId));
    }

    public async Task<bool> HasGroqAsync(Guid profileId) =>
        !string.IsNullOrWhiteSpace(await GetAsync(Key(profileId, "groq-key")));

    public async Task<string> GetTextModelAsync(Guid profileId)
    {
        string? saved = await GetAsync(Key(profileId, "text-model"));
        return saved is null or GroqSettings.RetiredTextModel ? Defaults.TextModel : saved;
    }

    public async Task<string> GetVisionModelAsync(Guid profileId) =>
        await GetAsync(Key(profileId, "vision-model")) ?? Defaults.VisionModel;

    public async Task<string> GetSpeechModelAsync(Guid profileId) =>
        await GetAsync(Key(profileId, "speech-model")) ?? Defaults.SpeechModel;

    public async Task SaveGroqAsync(Guid profileId, string? apiKey, string textModel, string visionModel, string speechModel)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await SetAsync(Key(profileId, "groq-key"), apiKey.Trim());
        }

        await SetAsync(Key(profileId, "text-model"), textModel);
        await SetAsync(Key(profileId, "vision-model"), visionModel);
        await SetAsync(Key(profileId, "speech-model"), speechModel);
    }

    public Task RemoveGroqAsync(Guid profileId) => RemoveAsync(Key(profileId, "groq-key"));

    public async Task<Guid?> GetSessionIdAsync(Guid profileId) =>
        Guid.TryParse(await GetAsync(Key(profileId, "session")), out Guid id) ? id : null;

    public Task SaveSessionIdAsync(Guid profileId, Guid sessionId) =>
        SetAsync(Key(profileId, "session"), sessionId.ToString("D"));

    public Task ClearSessionIdAsync(Guid profileId) => RemoveAsync(Key(profileId, "session"));

    internal static string Key(Guid profileId, string setting) => $"nutriflow.{setting}.{profileId:N}";

    private async Task<string?> GetAsync(string key) =>
        await js.InvokeAsync<string?>("localStorage.getItem", key);

    private async Task SetAsync(string key, string value) =>
        await js.InvokeVoidAsync("localStorage.setItem", key, value);

    private async Task RemoveAsync(string key) =>
        await js.InvokeVoidAsync("localStorage.removeItem", key);
}
