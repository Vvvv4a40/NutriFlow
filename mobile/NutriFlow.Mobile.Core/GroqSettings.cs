namespace NutriFlow.Mobile.Core;

public sealed record GroqSettings(
    string ApiKey,
    string TextModel = GroqSettings.DefaultTextModel,
    string VisionModel = "qwen/qwen3.8-27b",
    string SpeechModel = "whisper-large-v3-turbo")
{
    public const string DefaultTextModel = "openai/gpt-oss-120b";
    // limits are per model: when the main one is used up, the same key still has this one
    public const string ReserveTextModel = "openai/gpt-oss-20b";
    // the previous default mixed chinese characters into russian text; a profile that saved it moves to the new one
    public const string RetiredTextModel = "qwen/qwen3.8-27b";

    public override string ToString() => "Groq settings (credentials hidden)";
}
