using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Pages;

// texts of voice input on the food screen
public static class VoiceText
{
    public const int MaximumSeconds = 110;
    public const string NotRecognised = "Речь не распознана. Попробуйте говорить ближе к микрофону или введите текст.";
    public const string WrongFormat = "Браузер записал звук в формате, который Groq не принимает. Введите текст.";
    public const string StartFailed = "Не удалось начать запись. Обновите страницу или введите текст.";

    // the field stays available, so the way without Groq is typing the text
    public static string Error(Exception exception) =>
        CaptureText.GroqError(exception, "для голоса", "введите текст");

    public static string Chip(VoiceCapture voice) => voice.Phase switch
    {
        VoicePhase.Requesting => "Микрофон…",
        VoicePhase.Recording => $"Стоп · {voice.ElapsedSeconds / 60}:{voice.ElapsedSeconds % 60:00}",
        VoicePhase.Transcribing => "Распознаю…",
        _ => "Голос"
    };

    // starts with the visible words, so voice control finds the button by what it shows
    public static string ChipDescription(VoiceCapture voice) => voice.Phase switch
    {
        VoicePhase.Requesting => "Микрофон, жду доступ",
        VoicePhase.Recording => "Стоп, идёт запись",
        VoicePhase.Transcribing => "Распознаю запись",
        _ => "Голос"
    };

    // the last seconds before the recording stops in the middle of a sentence
    public static bool LimitIsNear(VoiceCapture voice) =>
        voice.Phase == VoicePhase.Recording && voice.ElapsedSeconds >= MaximumSeconds - 10;

    // the limit is the usual reason, but the browser may also end a recording, for example when a call comes in
    public static string StoppedByItself(int seconds) => seconds >= MaximumSeconds - 1
        ? $"Достигнут максимум {MaximumSeconds} с. Проверьте, всё ли вошло."
        : $"Запись прервалась на {seconds} с. Проверьте, всё ли вошло.";
}
