using Microsoft.Maui.Controls.Shapes;
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
        AddNavigationTab("Настройки", AppIconKind.Settings, 2);
        _navigation.Add(_addButton, 1);
        SetAddMenuOpen(false);
    }

    private Button CreateAddButton()
    {
        Button add = Ui.Action("+", () =>
        {
            if (!_busy && _profile is not null)
            {
                SetAddMenuOpen(!_addMenu.IsVisible);
            }
            return Task.CompletedTask;
        });
        add.AutomationId = "add-food";
        add.FontSize = 32;
        add.FontAttributes = FontAttributes.None;
        add.Padding = 0;
        add.CornerRadius = 30;
        add.WidthRequest = 60;
        add.HeightRequest = 60;
        add.HorizontalOptions = LayoutOptions.Center;
        add.VerticalOptions = LayoutOptions.Center;
        add.Shadow = new Shadow { Brush = new SolidColorBrush(Ui.Accent), Offset = new Point(0, 4), Radius = 14, Opacity = 0.22f };
        return add;
    }

    private View CreateAddMenu()
    {
        VerticalStackLayout actions = new() { Spacing = 4 };
        AddAction("Записать еду", "add-menu-input", "Ввод");
        AddAction("Продукты", "add-menu-products", "Продукты");
        AddAction("Мои блюда", "add-menu-dishes", "Блюда");
        return new Border
        {
            Content = new ScrollView { Content = actions, MaximumHeightRequest = 280 },
            BackgroundColor = Ui.Surface,
            Padding = 8,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 24 }
        };

        void AddAction(string title, string automationId, string tab)
        {
            Button button = Ui.Action(title, () => SelectTabAsync(tab), true);
            button.AutomationId = automationId;
            button.MinimumHeightRequest = 56;
            button.BackgroundColor = Ui.Surface;
            SemanticProperties.SetDescription(button, title);
            actions.Children.Add(button);
        }
    }

    private void SetAddMenuOpen(bool open)
    {
        _addMenu.IsVisible = open;
        _addButton.Text = open ? "×" : "+";
        SemanticProperties.SetDescription(_addButton, open ? "Закрыть меню добавления" : "Добавить еду");
        SemanticProperties.SetHint(_addButton, open ? "Скрыть способы добавления" : "Открыть меню: записать еду, продукты или мои блюда");
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

        if (_addMenu.IsVisible)
        {
            SetAddMenuOpen(false);
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
        Button restore = Ui.Action("Восстановить копию", () => RunAsync(RestoreBackupPageAsync), true);
        restore.AutomationId = "welcome-backup-restore";
        VerticalStackLayout actions = new() { Spacing = 10 };
        actions.Children.Add(create);
        actions.Children.Add(restore);
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
        welcome.Add(actions, 0, 3);
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
