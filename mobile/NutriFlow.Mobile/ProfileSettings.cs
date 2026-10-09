using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

internal static class ProfileSettings
{
    private static string Key(Guid profileId, string setting) => $"nutriflow.{setting}.{profileId:N}";

    public static string TextModel(Guid profileId) =>
        Preferences.Default.Get(Key(profileId, "text-model"), "qwen/qwen3.8-27b");

    public static string VisionModel(Guid profileId) =>
        Preferences.Default.Get(Key(profileId, "vision-model"), "qwen/qwen3.8-27b");

    public static string SpeechModel(Guid profileId) =>
        Preferences.Default.Get(Key(profileId, "speech-model"), "whisper-large-v3-turbo");

    public static async Task<GroqSettings> GetGroqAsync(Guid profileId)
    {
        string? apiKey = await SecureStorage.Default.GetAsync(Key(profileId, "groq-key"));
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Добавьте личный ключ Groq в разделе «Настройки». Для ручного ввода ключ не нужен.");
        }

        return new GroqSettings(apiKey, TextModel(profileId), VisionModel(profileId), SpeechModel(profileId));
    }

    public static async Task SaveGroqAsync(Guid profileId, string? apiKey, string textModel, string visionModel, string speechModel)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await SecureStorage.Default.SetAsync(Key(profileId, "groq-key"), apiKey.Trim());
        }

        Preferences.Default.Set(Key(profileId, "text-model"), textModel);
        Preferences.Default.Set(Key(profileId, "vision-model"), visionModel);
        Preferences.Default.Set(Key(profileId, "speech-model"), speechModel);
    }

    public static Task<bool> HasGroqAsync(Guid profileId) => HasSecretAsync(profileId);

    private static async Task<bool> HasSecretAsync(Guid profileId) =>
        !string.IsNullOrWhiteSpace(await SecureStorage.Default.GetAsync(Key(profileId, "groq-key")));

    public static void RemoveGroq(Guid profileId) => SecureStorage.Default.Remove(Key(profileId, "groq-key"));

    public static Guid? SessionId(Guid profileId)
    {
        string? value = Preferences.Default.Get(Key(profileId, "session"), "");
        return Guid.TryParse(value, out Guid id) ? id : null;
    }

    public static void SaveSession(Guid profileId, Guid id) =>
        Preferences.Default.Set(Key(profileId, "session"), id.ToString("D"));

    public static void ClearSession(Guid profileId) => Preferences.Default.Remove(Key(profileId, "session"));
}
