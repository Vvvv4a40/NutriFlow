using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Pages;

// texts of the voice form (AudioPageAsync in MainPage.Media.cs)
public static class VoiceText
{
    public const int MaximumSeconds = 110;
    public const string NotRecognised = "Речь не распознана. Попробуйте говорить ближе к микрофону или введите текст.";
    public const string WrongFormat = "Браузер записал звук в формате, который Groq не принимает. Введите текст.";
    public const string StartFailed = "Не удалось начать запись. Обновите страницу или введите текст.";
    public const string NoKey = "Чтобы распознать речь, добавьте личный ключ Groq в настройках профиля. Текст можно ввести и вручную.";
    public const string Busy = "Сначала остановите запись и дождитесь распознавания.";
    public const string Empty = "Запишите речь или введите текст.";

    // the field stays available, so the way without Groq is typing the text
    public static string Error(Exception exception) =>
        CaptureText.GroqError(exception, "для голоса", "введите текст");

    public static string Status(VoiceCapture voice) => voice.Phase switch
    {
        VoicePhase.Requesting => "Разрешите доступ к микрофону, если браузер спросит.",
        VoicePhase.Recording when LimitIsNear(voice) =>
            $"Запись: {voice.ElapsedSeconds} с. Осталось {MaximumSeconds - voice.ElapsedSeconds} с.",
        VoicePhase.Recording => $"Запись: {voice.ElapsedSeconds} с. Максимум {MaximumSeconds} с.",
        VoicePhase.Transcribing => "Распознаю запись…",
        _ when voice.CanRetry => "Запись сохранена — можно повторить распознавание, не диктуя заново.",
        _ when voice.Recognised && voice.StoppedByItself => StoppedByItself(voice.ElapsedSeconds),
        _ when voice.Recognised && !string.IsNullOrWhiteSpace(voice.Text) => "Текст готов — проверьте его перед использованием.",
        _ => $"Расскажите, что и сколько съели. Запись до {MaximumSeconds} секунд."
    };

    // the last seconds before the recording stops in the middle of a sentence
    public static bool LimitIsNear(VoiceCapture voice) =>
        voice.Phase == VoicePhase.Recording && voice.ElapsedSeconds >= MaximumSeconds - 10;

    // the limit is the usual reason, but the browser may also end a recording, for example when a call comes in
    public static string StoppedByItself(int seconds) => seconds >= MaximumSeconds - 1
        ? $"Достигнут максимум {MaximumSeconds} с. Проверьте, всё ли вошло."
        : $"Запись прервалась на {seconds} с. Проверьте, всё ли вошло.";

    public static string RecordButton(VoiceCapture voice) => voice.Phase switch
    {
        VoicePhase.Requesting => "Жду доступ к микрофону…",
        VoicePhase.Recording => "Остановить",
        VoicePhase.Transcribing => "Распознаю…",
        _ when voice.Recognised => "Продиктовать ещё",
        _ => "Начать запись"
    };
}
