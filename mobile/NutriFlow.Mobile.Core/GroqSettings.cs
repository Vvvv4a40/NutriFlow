using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Mobile.Core;

public sealed record GroqSettings(
    string ApiKey,
    string TextModel = GroqSettings.DefaultTextModel,
    string VisionModel = GroqModels.Vision,
    string SpeechModel = GroqModels.Speech)
{
    public const string DefaultTextModel = GroqModels.DefaultText;
    public const string ReserveTextModel = GroqModels.ReserveText;
    public const string RetiredTextModel = GroqModels.RetiredText;

    public override string ToString() => "Groq settings (credentials hidden)";
}
