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
        ArgumentException argument when IsRussian(argument.Message) => WithoutParameterName(argument),
        InvalidOperationException when IsRussian(exception.Message) => exception.Message,
        // the dialog title already says «Не удалось выполнить действие»
        _ => "Проверьте введённые данные и повторите."
    };

    private static bool IsRussian(string message) => message.Any(c => c is >= 'А' and <= 'я');

    // the message of ArgumentException ends with the parameter name, as «(Parameter 'name')» or, in a trimmed
    // browser build, «Arg_ParamName_Name, name»; the same suffix built for an empty message finds either form
    private static string WithoutParameterName(ArgumentException exception)
    {
        if (exception.ParamName is null)
        {
            return exception.Message;
        }

        string suffix = new ArgumentException("", exception.ParamName).Message;
        return exception.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? exception.Message[..^suffix.Length]
            : exception.Message;
    }
}
