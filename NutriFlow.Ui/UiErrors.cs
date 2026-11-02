using Microsoft.JSInterop;

namespace NutriFlow.Ui;

// what a failed action tells the user; own errors carry a russian message, everything else gets a next step
public static class UiErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        OperationCanceledException => "Запрос отменён или занял слишком много времени. Проверьте подключение и повторите.",
        HttpRequestException => "Сервис недоступен. Проверьте интернет, ключ Groq и выбранную модель, затем повторите запрос.",
        JSException when exception.Message.Contains("QuotaExceededError", StringComparison.Ordinal) =>
            "Не удалось сохранить: в браузере закончилось место. Освободите место на устройстве и повторите.",
        IOException => "Не удалось прочитать или сохранить файл. Проверьте свободное место и повторите.",
        ArgumentException or InvalidOperationException when exception.Message.Any(c => c is >= 'А' and <= 'я') => exception.Message,
        // the dialog title already says «Не удалось выполнить действие»
        _ => "Проверьте введённые данные и повторите."
    };
}
