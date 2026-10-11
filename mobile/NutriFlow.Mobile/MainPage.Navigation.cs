using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderNavigation()
    {
        _navigation.Children.Clear();
        _navigation.IsVisible = _profile is not null;
        _navigation.BackgroundColor = Ui.Background;
        _sectionLabel.Text = _profile is null ? "Питание в твоём ритме" : _tab == "Дневник" ? "Дневник питания" : _tab == "Ввод" ? "Добавить еду" : _tab;
        AddNavigationTab("Дневник", AppIconKind.Diary, 0);
        AddNavigationTab("Продукты", AppIconKind.Products, 1);
        AddNavigationTab("Блюда", AppIconKind.Dishes, 3);
        AddNavigationTab("Настройки", AppIconKind.Settings, 4);

        Button add = Ui.Action("+", () => SelectTabAsync("Ввод"));
        add.AutomationId = "add-food";
        SemanticProperties.SetDescription(add, "Добавить еду");
        SemanticProperties.SetHint(add, "Открыть ввод еды: текст, голос или вручную");
        add.FontSize = 32;
        add.FontAttributes = FontAttributes.None;
        add.Padding = 0;
        add.CornerRadius = 30;
        add.WidthRequest = 60;
        add.HeightRequest = 60;
        add.HorizontalOptions = LayoutOptions.Center;
        add.VerticalOptions = LayoutOptions.Center;
        add.Shadow = new Shadow { Brush = new SolidColorBrush(Ui.Accent), Offset = new Point(0, 4), Radius = 14, Opacity = 0.22f };
        _navigation.Add(add, 2);
    }

    private void AddNavigationTab(string tab, AppIconKind icon, int column)
    {
        bool selected = _tab == tab;
        Color color = selected ? Ui.Accent : Ui.Muted;
        Button button = Ui.Action("", () => SelectTabAsync(tab), true);
        button.AutomationId = $"tab-{icon.ToString().ToLowerInvariant()}";
        button.BackgroundColor = selected ? Ui.Surface : Colors.Transparent;
        button.Padding = 0;
        button.MinimumHeightRequest = 64;
        SemanticProperties.SetDescription(button, tab);
        SemanticProperties.SetHint(button, selected ? "Текущий раздел" : $"Открыть раздел «{tab}»");

        Label caption = Ui.Text(tab);
        caption.FontSize = 10;
        caption.TextColor = color;
        caption.HorizontalTextAlignment = TextAlignment.Center;
        caption.InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(caption, false);
        VerticalStackLayout content = new() { Spacing = 5, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        content.Children.Add(new AppIcon(icon, color) { HorizontalOptions = LayoutOptions.Center });
        content.Children.Add(caption);
        Grid tile = new();
        tile.Add(button);
        tile.Add(content);
        _navigation.Add(tile, column);
    }

    private Task SelectTabAsync(string tab) => RunAsync(async () =>
    {
        _tab = tab;
        await RenderAsync();
        await _scroll.ScrollToAsync(0, 0, false);
    });

    private Task OpenProfilesAsync() => RunAsync(async () =>
    {
        IReadOnlyList<LocalProfile> profiles = await _profiles.ListAsync();
        string[] choices = profiles.Select((profile, index) => $"{index + 1}. {profile.Name}").ToArray();
        string? choice = await DisplayActionSheetAsync("Локальные профили", "Отмена", null, [.. choices, "Создать профиль"]);
        if (choice == "Создать профиль")
        {
            if (await CanLeaveDraftTextAsync())
            {
                await CreateProfilePageAsync();
            }
            return;
        }

        int selectedIndex = Array.IndexOf(choices, choice);
        if (selectedIndex < 0 || profiles[selectedIndex].Id == _profile?.Id)
        {
            return;
        }

        if (!await CanLeaveDraftTextAsync())
        {
            return;
        }

        _profile = await _profiles.SelectAsync(profiles[selectedIndex].Id);
        _session = null;
        _draftText = "";
        await ResumeSessionAsync(_profile.Id);
        await RefreshProfilesAsync();
        await RenderAsync();
    });

    private Task<bool> CanLeaveDraftTextAsync() => string.IsNullOrWhiteSpace(_draftText)
        ? Task.FromResult(true)
        : DisplayAlertAsync("Сменить профиль?", "Набранный, но ещё не отправленный текст будет очищен. Сохранённый черновик останется в своём профиле.", "Сменить", "Отмена");

    protected override bool OnBackButtonPressed()
    {
        if (_busy)
        {
            return true;
        }

        if (_profile is not null && _tab != "Дневник")
        {
            _ = SelectTabAsync("Дневник");
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private void RenderWelcome()
    {
        Label eyebrow = Ui.Text("МЕНЬШЕ РУТИНЫ. БОЛЬШЕ ЯСНОСТИ.", true);
        eyebrow.FontSize = 11;
        eyebrow.CharacterSpacing = 1.2;
        eyebrow.Margin = new Thickness(0, 28, 0, 0);
        Label title = Ui.Title("Питание под вашим контролем");
        title.FontSize = 36;
        title.CharacterSpacing = -1;
        title.Margin = new Thickness(0, 0, 12, 0);
        _body.Children.Add(eyebrow);
        _body.Children.Add(title);
        _body.Children.Add(Ui.Text("Расскажите, что приготовили. NutriFlow поможет собрать состав, а вы проверите результат перед записью.", true));

        Border welcome = Ui.Card(
            Ui.Title("Ваш день. Ваши данные."),
            Ui.Text("Дневник, продукты и блюда сохраняются на телефоне. Ручной ввод работает даже без интернета.", true));
        welcome.Margin = new Thickness(0, 8, 0, 8);
        _body.Children.Add(welcome);
        Button create = Ui.Action("Создать профиль", CreateProfilePageAsync);
        create.AutomationId = "create-profile";
        _body.Children.Add(create);
        Label note = Ui.Text("Для текста, голоса и фото этикеток можно подключить личный Groq-ключ в настройках.", true);
        note.FontSize = 12;
        _body.Children.Add(note);
    }

    private static Grid IconAction(AppIconKind icon, string description, Func<Task> action)
    {
        Button button = Ui.Action("", action, true);
        button.BackgroundColor = Colors.Transparent;
        button.Padding = 0;
        SemanticProperties.SetDescription(button, description);
        Grid control = new() { WidthRequest = 48, MinimumHeightRequest = 48 };
        control.Add(button);
        control.Add(new AppIcon(icon, Ui.Muted) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center });
        return control;
    }
}
