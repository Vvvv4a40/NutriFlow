using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private static readonly (string Tab, AppIconKind Icon, int Column)[] Tabs =
    [
        ("Дневник", AppIconKind.Diary, 0),
        ("Продукты", AppIconKind.Products, 1),
        ("Блюда", AppIconKind.Dishes, 3),
        ("Настройки", AppIconKind.Settings, 4)
    ];

    private void CreateNavigation()
    {
        for (int index = 0; index < 5; index++)
        {
            _navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }
    }

    private void RenderNavigation()
    {
        _navigation.Children.Clear();
        foreach ((string tab, AppIconKind icon, int column) in Tabs)
        {
            _navigation.Add(NavigationTab(tab, icon), column);
        }

        _navigation.Add(CreateAddButton(), 2);
    }

    // logging food is the most frequent task, so the central button opens it directly
    private Button CreateAddButton()
    {
        Button add = new()
        {
            Text = "+",
            AutomationId = "add-food",
            FontSize = 30,
            TextColor = Ui.OnAccent,
            BackgroundColor = Ui.Accent,
            Padding = 0,
            CornerRadius = 28,
            WidthRequest = 56,
            HeightRequest = 56,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Shadow = new Shadow { Brush = new SolidColorBrush(Ui.Accent), Offset = new Point(0, 4), Radius = 16, Opacity = 0.28f }
        };
        SemanticProperties.SetDescription(add, "Добавить еду");
        SemanticProperties.SetHint(add, "Описать еду текстом или голосом, выбрать продукт или блюдо");
        add.Clicked += async (_, _) =>
        {
            if (!Busy && _profile is not null)
            {
                await SelectTabAsync("Ввод");
            }
        };
        return add;
    }

    private View NavigationTab(string tab, AppIconKind icon)
    {
        bool selected = _tab == tab;
        Color color = selected ? Ui.Accent : Ui.Subtle;
        Border indicator = new()
        {
            WidthRequest = 56,
            HeightRequest = 30,
            HorizontalOptions = LayoutOptions.Center,
            BackgroundColor = selected ? Ui.AccentSoft : Colors.Transparent,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 15 },
            Content = new AppIcon(icon, color, 22) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
        };
        Label caption = new()
        {
            Text = tab,
            FontSize = 12,
            FontAutoScalingEnabled = false,
            FontFamily = Ui.MediumFont,
            TextColor = color,
            HorizontalTextAlignment = TextAlignment.Center,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        VerticalStackLayout content = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center, Padding = new Thickness(0, 6) };
        content.Children.Add(indicator);
        content.Children.Add(caption);
        Grid tile = Ui.Pressable(content, () => SelectTabAsync(tab), tab,
            selected ? "Текущий раздел" : $"Открыть раздел «{tab}»", $"tab-{icon.ToString().ToLowerInvariant()}");
        ((Button)tile.Children[0]).CornerRadius = 18;
        tile.MinimumHeightRequest = 64;
        return tile;
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
        string[] choices = profiles.Select((profile, index) => profile.Id == _profile?.Id
            ? $"{index + 1}. {profile.Name} ✓" : $"{index + 1}. {profile.Name}").ToArray();
        string? choice = await DisplayActionSheetAsync("Профили на этом телефоне", "Отмена", null, [.. choices, "Создать профиль"]);
        if (choice == "Создать профиль")
        {
            await FlushInputDraftAsync();
            await CreateProfilePageAsync();
            return;
        }

        int selectedIndex = Array.IndexOf(choices, choice);
        if (selectedIndex < 0 || profiles[selectedIndex].Id == _profile?.Id)
        {
            return;
        }

        await FlushInputDraftAsync();
        _inputEditor = null;
        _profile = await _profiles.SelectAsync(profiles[selectedIndex].Id);
        _session = null;
        _draftText = "";
        _purpose = NutriFlow.Domain.MealSessionPurpose.Diary;
        _date = DateOnly.FromDateTime(DateTime.Today);
        await ResumeSessionAsync(_profile.Id);
        await LoadInputDraftAsync(_profile.Id);
        await RefreshProfilesAsync();
        await RenderAsync();
    });

    protected override bool OnBackButtonPressed()
    {
        if (Busy)
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
        Border mark = Ui.IconTile(AppIconKind.Sparkle, Ui.Accent, 72);
        mark.HorizontalOptions = LayoutOptions.Start;
        Label title = new()
        {
            Text = "NutriFlow",
            AutomationId = "welcome-title",
            FontSize = 44,
            FontAttributes = FontAttributes.Bold,
            CharacterSpacing = -1.4,
            TextColor = Ui.TextPrimary
        };
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        Label tagline = Ui.Text("Опишите еду своими словами — калории и БЖУ посчитаются по весу.", true);
        tagline.FontSize = 18;
        VerticalStackLayout intro = new() { Spacing = 14 };
        intro.Children.Add(mark);
        intro.Children.Add(title);
        intro.Children.Add(tagline);

        VerticalStackLayout features = new() { Spacing = 16, Margin = new Thickness(0, 12, 0, 0) };
        features.Children.Add(WelcomeFeature(AppIconKind.Microphone, "Текст, голос или фото этикетки"));
        features.Children.Add(WelcomeFeature(AppIconKind.Check, "Калории считаются по весу, оценки помечены"));
        features.Children.Add(WelcomeFeature(AppIconKind.Archive, "Данные хранятся только на этом телефоне"));
        intro.Children.Add(features);

        Button create = Ui.Action("Начать", () => RunAsync(CreateProfilePageAsync));
        create.AutomationId = "create-profile";
        create.MinimumHeightRequest = 56;
        Button restore = Ui.Ghost("Восстановить из копии", () => RunAsync(RestoreBackupPageAsync));
        restore.AutomationId = "welcome-backup-restore";
        VerticalStackLayout actions = new() { Spacing = 6 };
        actions.Children.Add(create);
        actions.Children.Add(restore);
        Grid welcome = new()
        {
            Padding = new Thickness(28, 24, 28, 20),
            RowSpacing = 32,
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Star }, new() { Height = GridLength.Auto },
                new() { Height = new GridLength(1.4, GridUnitType.Star) }, new() { Height = GridLength.Auto }
            }
        };
        welcome.Add(intro, 0, 1);
        welcome.Add(actions, 0, 3);
        // the grid fills the screen to keep the start button low, and scrolls on small phones or with large system fonts
        ScrollView scroll = new() { Content = welcome };
        scroll.SizeChanged += (_, _) => welcome.MinimumHeightRequest = scroll.Height;
        _welcome.Content = scroll;
    }

    private static View WelcomeFeature(AppIconKind icon, string text)
    {
        Grid row = new()
        {
            ColumnSpacing = 14,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        row.Add(Ui.IconTile(icon, Ui.Accent, 36), 0);
        Label label = Ui.Text(text);
        label.VerticalOptions = LayoutOptions.Center;
        row.Add(label, 1);
        return row;
    }
}
