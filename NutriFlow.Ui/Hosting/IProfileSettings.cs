namespace NutriFlow.Ui.Hosting;

// per-profile settings kept by the host outside the profile database, with the keys and defaults of the android app
public interface IProfileSettings
{
    Task<string> GetThemeAsync(Guid profileId);

    Task SaveThemeAsync(Guid profileId, string theme);

    Task<bool> HasGroqAsync(Guid profileId);

    Task<string> GetTextModelAsync(Guid profileId);

    Task<string> GetVisionModelAsync(Guid profileId);

    Task<string> GetSpeechModelAsync(Guid profileId);

    // an empty key keeps the stored one
    Task SaveGroqAsync(Guid profileId, string? apiKey, string textModel, string visionModel, string speechModel);

    Task RemoveGroqAsync(Guid profileId);
}
