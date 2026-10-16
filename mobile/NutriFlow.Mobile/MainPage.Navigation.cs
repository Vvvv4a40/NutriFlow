using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderNavigation()
    {
        _navigation.Children.Clear();
        _navigation.IsVisible = _profile is not null;
        _navigation.BackgroundColor = Ui.Background;
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
        Label title = Ui.Title("NutriFlow");
        title.AutomationId = "welcome-title";
        title.FontSize = 44;
        title.CharacterSpacing = -1.4;
        title.HorizontalOptions = LayoutOptions.Center;
        title.Margin = 0;
        Button create = Ui.Action("Начать", () => RunAsync(CreateProfilePageAsync));
        create.AutomationId = "create-profile";
        create.MinimumHeightRequest = 56;
        Grid welcome = new()
        {
            Padding = new Thickness(28, 24, 28, 28),
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Star }, new() { Height = GridLength.Auto },
                new() { Height = GridLength.Star }, new() { Height = GridLength.Auto }
            }
        };
        welcome.Add(title, 0, 1);
        welcome.Add(create, 0, 3);
        _welcome.Content = welcome;
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
