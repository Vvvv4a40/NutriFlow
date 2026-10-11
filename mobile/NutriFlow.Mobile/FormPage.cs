namespace NutriFlow.Mobile;

internal sealed class FormPage : ContentPage
{
    private readonly ActivityIndicator _activity = new() { Color = Ui.Accent, IsVisible = false };
    private readonly Label _status = Ui.Text("", true);
    private bool _busy;

    public FormPage(string title)
    {
        Title = title;
        BackgroundColor = Ui.Background;
        _status.IsVisible = false;
        Fields = new VerticalStackLayout { Padding = new Thickness(20, 16, 20, 32), Spacing = 16 };
        Fields.Children.Add(_activity);
        Fields.Children.Add(_status);
        Content = new ScrollView { Content = Fields };
    }

    public VerticalStackLayout Fields { get; }
    public Action? OnClose { get; set; }

    protected override void OnDisappearing()
    {
        OnClose?.Invoke();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed() => _busy || base.OnBackButtonPressed();

    public async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        NavigationPage.SetHasBackButton(this, false);
        Fields.IsEnabled = false;
        _activity.IsVisible = true;
        _activity.IsRunning = true;
        _status.Text = "Выполняется…";
        _status.IsVisible = true;
        try
        {
            await action();
            _status.Text = "";
            _status.IsVisible = false;
        }
        catch (Exception exception)
        {
            _status.Text = "Действие не завершено";
            await DisplayAlertAsync("Не удалось выполнить действие", ErrorText(exception), "Понятно");
        }
        finally
        {
            _activity.IsRunning = false;
            _activity.IsVisible = false;
            Fields.IsEnabled = true;
            _busy = false;
            NavigationPage.SetHasBackButton(this, true);
        }
    }

    public static string ErrorText(Exception exception) => exception switch
    {
        OperationCanceledException => "Запрос отменён или занял слишком много времени. Проверьте подключение и повторите.",
        HttpRequestException => "Сервис недоступен. Проверьте интернет, ключ Groq и выбранную модель, затем повторите запрос.",
        PermissionException => "Доступ не разрешён. Выдайте приложению разрешение в настройках телефона.",
        FeatureNotSupportedException => "Эта функция недоступна на устройстве. Выберите файл вручную.",
        IOException => "Не удалось прочитать или сохранить файл. Проверьте свободное место и повторите.",
        ArgumentException or InvalidOperationException when exception.Message.Any(c => c is >= 'А' and <= 'я') => exception.Message,
        _ => "Не удалось выполнить действие. Проверьте введённые данные и повторите."
    };
}
