namespace NutriFlow.Infrastructure.Ai;

public static class GroqModels
{
    public const string DefaultText = "openai/gpt-oss-120b";
    // limits are per model: when the main one is used up, the same key still has this one
    public const string ReserveText = "openai/gpt-oss-20b";
    // the previous default mixed chinese characters into russian text; a saved choice of it moves to the new one
    public const string RetiredText = "qwen/qwen3.8-27b";
    public const string Vision = "qwen/qwen3.8-27b";
    public const string Speech = "whisper-large-v3-turbo";

    public static string ReserveFor(string textModel) => textModel == ReserveText ? DefaultText : ReserveText;
}
