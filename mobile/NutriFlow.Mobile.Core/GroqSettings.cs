namespace NutriFlow.Mobile.Core;

public sealed record GroqSettings(
    string ApiKey,
    string TextModel = "qwen/qwen3.8-27b",
    string VisionModel = "qwen/qwen3.8-27b",
    string SpeechModel = "whisper-large-v3-turbo")
{
    public override string ToString() => "Groq settings (credentials hidden)";
}
