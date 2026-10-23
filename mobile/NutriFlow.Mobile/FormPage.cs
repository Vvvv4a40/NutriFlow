namespace NutriFlow.Mobile;

internal sealed class FormPage : ContentPage
{
    private readonly ActivityIndicator _activity = new() { Color = Ui.Accent, IsVisible = false, WidthRequest = 22, HeightRequest = 22 };
    private readonly BoxView _blocker = new() { Color = Colors.Transparent, IsVisible = false };
    private readonly VerticalStackLayout _footer = new() { Padding = new Thickness(Ui.ScreenPadding, 12, Ui.ScreenPadding, 16), Spacing = 8 };
    private readonly Grid _footerArea = new() { IsVisible = false, BackgroundColor = Ui.Background };
    private readonly Label _title;
    private readonly ActionRunner _runner;

    public FormPage(string title)
    {
        Title = title;
        BackgroundColor = Ui.Background;
        NavigationPage.SetHasNavigationBar(this, false);
#if ANDROID
        SafeAreaEdges = SafeAreaEdges.All;
#endif
        _title = Ui.Headline(title, 20);
        _title.VerticalOptions = LayoutOptions.Center;
        _title.LineBreakMode = LineBreakMode.TailTruncation;
        _title.MaxLines = 1;
        Grid header = new()
        {
            Padding = new Thickness(6, 6, 16, 6),
            ColumnSpacing = 4,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }
            }
        };
        header.Add(Ui.IconButton(AppIconKind.Previous, "Назад", CloseAsync, Ui.TextPrimary), 0);
        header.Add(_title, 1);
        header.Add(_activity, 2);

        Fields = new VerticalStackLayout { Padding = new Thickness(Ui.ScreenPadding, 8, Ui.ScreenPadding, 32), Spacing = 18 };
        _footerArea.Add(new BoxView { HeightRequest = 1, Color = Ui.Stroke, VerticalOptions = LayoutOptions.Start });
        _footerArea.Add(_footer);
        Grid layout = new()
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, new() { Height = GridLength.Star }, new() { Height = GridLength.Auto }
            }
        };
        layout.Add(header, 0, 0);
        layout.Add(new ScrollView { Content = Fields }, 0, 1);
        layout.Add(_footerArea, 0, 2);
        layout.Add(_blocker, 0, 0);
        Grid.SetRowSpan(_blocker, 3);
        Content = layout;
        _runner = new ActionRunner(this, _activity, _blocker);
    }

    public VerticalStackLayout Fields { get; }
    public Action? OnClose { get; set; }

    public void AddFooter(View view)
    {
        _footer.Children.Add(view);
        _footerArea.IsVisible = true;
    }

    protected override void OnDisappearing()
    {
        OnClose?.Invoke();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed() => _runner.IsBusy || base.OnBackButtonPressed();

    private Task CloseAsync() => _runner.IsBusy ? Task.CompletedTask : Navigation.PopAsync();

    public Task RunAsync(Func<Task> action) => _runner.RunAsync(action);

    public void ShowProgress(Button button, string text) => _runner.ShowProgress(button, text);

    public new Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        _runner.ConfirmAsync(title, message, accept, cancel);

    public new Task DisplayAlertAsync(string title, string message, string cancel) =>
        _runner.AlertAsync(title, message, cancel);

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
