namespace NutriFlow.Ui;

// the cause of a handled failure, for the browser console or the debugger. Not Console.Error: in webassembly every line
// written there also opens the fatal bar «Не удалось продолжить работу», though the screen already says what to do
public static class UiLog
{
    public static void Error(object? cause) => Console.WriteLine(cause);
}
