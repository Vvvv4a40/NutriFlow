using Microsoft.JSInterop;
using NutriFlow.Ui.Dialogs;

namespace NutriFlow.Ui.Tests;

public sealed class DialogServiceTests
{
    [Fact]
    public async Task ConfirmationIsTrueOnlyForTheAcceptButton()
    {
        DialogService dialogs = new();

        Task<bool> accepted = dialogs.ConfirmAsync("Удалить запись?", "Запись исчезнет из дневника.", "Удалить", "Оставить");
        DialogRequest request = dialogs.Current!;
        Assert.Equal([new DialogButton("Удалить", DialogButtonRole.Primary)], request.Buttons);
        Assert.Equal("Оставить", request.Cancel);
        Assert.False(request.Sheet);
        dialogs.Resolve(request, "Удалить");
        Assert.True(await accepted);

        Task<bool> cancelled = dialogs.ConfirmAsync("Удалить запись?", "Запись исчезнет из дневника.", "Удалить", "Оставить", destructive: true);
        Assert.Equal(DialogButtonRole.Destructive, dialogs.Current!.Buttons[0].Role);
        dialogs.Resolve(dialogs.Current!, null);
        Assert.False(await cancelled);
    }

    [Fact]
    public async Task ActionSheetPutsTheDestructiveChoiceLastAndAnswersWithItsText()
    {
        DialogService dialogs = new();

        Task<string?> choice = dialogs.ChooseAsync("Гречка, 150 г", "Отмена", "Удалить", "Изменить вес");
        DialogRequest request = dialogs.Current!;

        Assert.True(request.Sheet);
        Assert.Equal(["Изменить вес", "Удалить"], request.Buttons.Select(button => button.Text));
        Assert.Equal(DialogButtonRole.Destructive, request.Buttons[^1].Role);
        dialogs.Resolve(request, "Изменить вес");
        Assert.Equal("Изменить вес", await choice);
    }

    [Fact]
    public async Task DialogsWaitInLineAndAStaleAnswerIsIgnored()
    {
        DialogService dialogs = new();
        int changes = 0;
        dialogs.Changed += () => changes++;

        Task first = dialogs.AlertAsync("Первое", "Текст", "Понятно");
        DialogRequest firstRequest = dialogs.Current!;
        Task second = dialogs.AlertAsync("Второе", "Текст", "Понятно");
        Assert.Same(firstRequest, dialogs.Current);

        dialogs.Resolve(firstRequest, null);
        dialogs.Resolve(firstRequest, null);
        await first;
        Assert.Equal("Второе", dialogs.Current!.Title);
        Assert.False(second.IsCompleted);

        dialogs.Resolve(dialogs.Current, null);
        await second;
        Assert.Null(dialogs.Current);
        Assert.Equal(4, changes);
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public void ErrorsTellWhatToDoNext(Exception exception, string expected) => Assert.Equal(expected, UiErrors.Describe(exception));

    public static TheoryData<Exception, string> Errors => new()
    {
        { new TaskCanceledException(), "Запрос отменён или занял слишком много времени. Проверьте подключение и повторите." },
        { new HttpRequestException("timeout"), "Сервис недоступен. Проверьте интернет, ключ Groq и выбранную модель, затем повторите запрос." },
        { new JSException("QuotaExceededError: The quota has been exceeded."), "Не удалось сохранить: в браузере закончилось место. Освободите место на устройстве и повторите." },
        { new IOException("disk"), "Не удалось прочитать или сохранить файл. Проверьте свободное место и повторите." },
        { new ArgumentException("Вес: введите положительное число."), "Вес: введите положительное число." },
        // LocalProfileStore passes nameof(name); the user should not see «(Parameter 'name')»
        { new ArgumentException("Профиль с таким названием уже существует.", "name"), "Профиль с таким названием уже существует." },
        { new InvalidOperationException("На этом устройстве уже создано 30 профилей."), "На этом устройстве уже создано 30 профилей." },
        { new ArgumentNullException("name"), "Проверьте введённые данные и повторите." },
        { new InvalidOperationException("Sequence contains no elements"), "Проверьте введённые данные и повторите." }
    };
}
