using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage : ContentPage
{
    private readonly LocalProfileStore _profiles;
    private readonly LocalMealClient _client;
    private readonly Button _profileButton;
    private readonly Label _profileInitial;
    private readonly Label _screenTitle;
    private readonly Grid _header;
    private readonly VerticalStackLayout _body = new() { Padding = new Thickness(Ui.ScreenPadding, 4, Ui.ScreenPadding, 32), Spacing = 16 };
    private readonly Grid _navigation = new() { Padding = new Thickness(6, 6, 6, 8) };
    private readonly Grid _navigationArea = new();
    private readonly ContentView _actionBar = new() { Padding = new Thickness(Ui.ScreenPadding, 10, Ui.ScreenPadding, 10) };
    private readonly Grid _actionBarArea = new() { IsVisible = false };
    private readonly BoxView _actionBarLine = new() { HeightRequest = 1, VerticalOptions = LayoutOptions.Start };
    private readonly BoxView _navigationLine = new() { HeightRequest = 1, VerticalOptions = LayoutOptions.Start };
    private readonly Border _avatar;
    private readonly ScrollView _scroll;
    private readonly ContentView _welcome = new() { IsVisible = false };
    private readonly ActivityIndicator _activity = new() { IsVisible = false, WidthRequest = 22, HeightRequest = 22 };
    private readonly BoxView _blocker = new() { Color = Colors.Transparent, IsVisible = false };
    private LocalProfile? _profile;
    private MealSessionResponse? _session;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private MealSessionPurpose _purpose;
    private string _draftText = "";
    private string _tab = "Дневник";
    private readonly ActionRunner _runner;
    private bool _scrollToPreview;

    public MainPage(LocalProfileStore profiles, LocalMealClient client)
    {
        InitializeComponent();
        _profiles = profiles;
        _client = client;
        _inputDrafts = new LocalInputDraftStore(profiles);
        Title = "NutriFlow";
#if ANDROID
        SafeAreaEdges = SafeAreaEdges.All;
#endif
        NavigationPage.SetHasNavigationBar(this, false);

        _screenTitle = Ui.Title("Дневник");
        _screenTitle.VerticalOptions = LayoutOptions.Center;
        _screenTitle.MaxLines = 1;
        _screenTitle.LineBreakMode = LineBreakMode.TailTruncation;
        // an avatar instead of a name chip leaves the header to the screen title on narrow phones;
        // the full name stays in the accessible description, the profile sheet and settings
        _profileInitial = new Label
        {
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            FontAutoScalingEnabled = false,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        _avatar = new Border
        {
            WidthRequest = 40,
            HeightRequest = 40,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Content = _profileInitial
        };
        Grid profileChip = Ui.Pressable(_avatar, OpenProfilesAsync, "Локальные профили", "Сменить или создать профиль", "profile-switcher");
        profileChip.WidthRequest = 48;
        profileChip.HeightRequest = 48;
        profileChip.VerticalOptions = LayoutOptions.Center;
        _profileButton = (Button)profileChip.Children[0];
        _profileButton.CornerRadius = 24;

        _header = new Grid
        {
            Padding = new Thickness(Ui.ScreenPadding, 10, 16, 8),
            ColumnSpacing = 10,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto }
            }
        };
        _header.Add(_screenTitle, 0);
        _header.Add(_activity, 1);
        _header.Add(profileChip, 2);

        _scroll = new ScrollView { Content = _body };
        _actionBarArea.Add(_actionBarLine);
        _actionBarArea.Add(_actionBar);
        _navigationArea.Add(_navigation);
        _navigationArea.Add(_navigationLine);
        CreateNavigation();

        Grid layout = new()
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, new() { Height = GridLength.Star },
                new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto }
            }
        };
        layout.Add(_header, 0, 0);
        layout.Add(_scroll, 0, 1);
        layout.Add(_actionBarArea, 0, 2);
        layout.Add(_navigationArea, 0, 3);
        layout.Add(_welcome, 0, 0);
        Grid.SetRowSpan(_welcome, 4);
        layout.Add(_blocker, 0, 0);
        Grid.SetRowSpan(_blocker, 4);
        Content = layout;
        _runner = new ActionRunner(this, _activity, _blocker);
    }

    // controls built once in the constructor are recoloured here; everything else is rebuilt by RenderAsync
    internal void ApplyTheme()
    {
        ApplyShellColors();
        _ = RunAsync(RenderAsync);
    }

    internal void ApplyShellColors()
    {
        BackgroundColor = Ui.Background;
        _screenTitle.TextColor = Ui.TextPrimary;
        _activity.Color = Ui.Accent;
        _avatar.BackgroundColor = Ui.AccentSoft;
        _avatar.Stroke = Ui.Accent.WithAlpha(0.35f);
        _profileInitial.TextColor = Ui.Accent;
        Ui.ApplyStates(_profileButton, Ui.SurfacePressed, Ui.TextPrimary);
        _navigation.BackgroundColor = Ui.Background;
        _actionBarArea.BackgroundColor = Ui.Background;
        _navigationLine.Color = Ui.Stroke;
        _actionBarLine.Color = Ui.Stroke;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(async () =>
        {
            LocalProfile? selected = await _profiles.GetSelectedAsync();
            if (selected?.Id != _profile?.Id)
            {
                await FlushInputDraftAsync();
                _inputEditor = null;
                _profile = selected;
                _session = null;
                _draftText = "";
                _purpose = MealSessionPurpose.Diary;
                _date = DateOnly.FromDateTime(DateTime.Today);
                if (selected is not null)
                {
                    await ResumeSessionAsync(selected.Id);
                    await LoadInputDraftAsync(selected.Id);
                }
            }
            else if (_profile is not null && _session is not null)
            {
                Guid profileId = _profile.Id;
                Guid sessionId = _session.Id;
                _session = await _client.FindSessionAsync(profileId, sessionId);
            }

            await RefreshProfilesAsync();
            await RenderAsync();
        });
    }

    private Task RefreshProfilesAsync()
    {
        string name = _profile?.Name ?? "";
        _profileInitial.Text = name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
        SemanticProperties.SetDescription(_profileButton, _profile is null ? "Локальные профили" : $"Профиль: {_profile.Name}. Сменить профиль");
        return Task.CompletedTask;
    }

    private async Task ResumeSessionAsync(Guid profileId)
    {
        if (ProfileSettings.SessionId(profileId) is not Guid id)
        {
            return;
        }

        MealSessionResponse? session = await _client.FindSessionAsync(profileId, id);
        if (session is not null && session.Status != "Confirmed")
        {
            _session = session;
            _date = session.MealDate;
            _purpose = session.Purpose == "CreateDish" ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary;
        }
        else
        {
            ProfileSettings.ClearSession(profileId);
        }
    }

    private bool Busy => _runner.IsBusy;

    private Task RunAsync(Func<Task> action) => _runner.RunAsync(action);

    private void ShowProgress(Button button, string text) => _runner.ShowProgress(button, text);

    // dialogs go through the runner so the spinner does not keep turning behind them
    private new Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        _runner.ConfirmAsync(title, message, accept, cancel);

    private new Task DisplayAlertAsync(string title, string message, string cancel) =>
        _runner.AlertAsync(title, message, cancel);

    private new Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons) =>
        _runner.ChooseAsync(title, cancel, destruction, buttons);

    private Guid CurrentProfileId() => _profile?.Id ?? throw new InvalidOperationException("Сначала создайте или выберите профиль.");

    private void SetActionBar(View? content)
    {
        _actionBar.Content = content;
        _actionBarArea.IsVisible = content is not null;
    }

    private string ScreenTitle() => _tab switch
    {
        "Ввод" when _session is not null => _session.Purpose == "CreateDish" ? "Проверьте блюдо" : "Проверьте запись",
        "Ввод" => _purpose == MealSessionPurpose.CreateDish ? "Новое блюдо" : "Запись еды",
        "Продукты" => "Продукты",
        "Блюда" => "Блюда",
        "Настройки" => "Настройки",
        _ => "Дневник"
    };

    private async Task RenderAsync()
    {
        _inputEditor = null;
        _inputSaveStatus = null;
        _inputSaveRetry = null;
        RenderNavigation();
        SetActionBar(null);
        _body.Children.Clear();
        bool hasProfile = _profile is not null;
        _header.IsVisible = hasProfile;
        _scroll.IsVisible = hasProfile;
        _navigationArea.IsVisible = hasProfile;
        _welcome.IsVisible = !hasProfile;
        if (_profile is null)
        {
            RenderWelcome();
            return;
        }

        _welcome.Content = null;
        _screenTitle.Text = ScreenTitle();
        Guid profileId = _profile.Id;
        if (InputDraftBlocked && _tab != "Ввод")
        {
            _body.Children.Add(Notice(AppIconKind.Warning, Ui.Warning, _draftReadError ?? "Есть неотправленный текст другой записи.",
                Ui.Ghost("Проверить сохранённый ввод", () => SelectTabAsync("Ввод")), "input-draft-attention"));
        }

        switch (_tab)
        {
            case "Дневник": await RenderDiaryAsync(profileId); break;
            case "Ввод": RenderInput(profileId, await ProfileSettings.HasGroqAsync(profileId)); break;
            case "Продукты": await RenderProductsAsync(profileId); break;
            case "Блюда": await RenderDishesAsync(profileId); break;
            case "Настройки": await RenderSettingsAsync(profileId); break;
        }

        if (_scrollToPreview && _tab == "Ввод")
        {
            _scrollToPreview = false;
            await _scroll.ScrollToAsync(0, 0, false);
        }
    }

    private static View Notice(AppIconKind icon, Color color, string message, View? action = null, string? automationId = null)
    {
        Grid content = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        content.Add(new AppIcon(icon, color, 22) { VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 1, 0, 0) }, 0);
        VerticalStackLayout text = new() { Spacing = 6 };
        text.Children.Add(Ui.Text(message));
        if (action is Button)
        {
            action.HorizontalOptions = LayoutOptions.Start;
            action.Margin = new Thickness(-12, 0, 0, -8);
        }

        if (action is not null)
        {
            text.Children.Add(action);
        }

        content.Add(text, 1);
        Border notice = new()
        {
            Content = content,
            Padding = new Thickness(16, 14),
            BackgroundColor = color.WithAlpha(0.08f),
            Stroke = color.WithAlpha(0.35f),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 18 }
        };
        if (automationId is not null)
        {
            notice.AutomationId = automationId;
        }

        return notice;
    }

    private Task CreateProfilePageAsync()
    {
        FormPage page = new("Новый профиль");
        Label title = Ui.Title("Как вас зовут?");
        page.Fields.Children.Add(title);
        page.Fields.Children.Add(Ui.Text("Имя видно только вам и помогает различать профили на этом телефоне.", true));
        Entry name = Ui.Field("Имя");
        name.AutomationId = "profile-name";
        name.MaxLength = 80;
        SemanticProperties.SetDescription(name, "Ваше имя");
        page.Fields.Children.Add(name);
        Button create = Ui.Action("Продолжить", () => page.RunAsync(async () =>
        {
            await FlushInputDraftAsync();
            LocalProfile created = await _profiles.CreateAsync(Ui.Name(name, "Имя"));
            await _profiles.SelectAsync(created.Id);
            await Navigation.PopAsync();
        }));
        create.AutomationId = "profile-create";
        page.AddFooter(create);
        return Navigation.PushAsync(page);
    }

    private Task EditEntryPageAsync(Guid profileId, MealEntryResponse entry)
    {
        FormPage page = new("Изменить запись");
        page.Fields.Children.Add(Ui.Headline(entry.Name, 24));
        page.Fields.Children.Add(Ui.NutritionBlock(entry.Nutrition, "Сейчас"));
        Entry grams = Ui.Field("", Ui.Amount(entry.WeightInGrams), true);
        page.Fields.Children.Add(Ui.Labeled("Вес порции, г", grams));
        page.Fields.Children.Add(Ui.Caption("КБЖУ пересчитаются по сохранённому составу."));
        Switch estimated = new();
        bool qualityChanged = false;
        estimated.Toggled += (_, _) => qualityChanged = true;
        page.Fields.Children.Add(Ui.SwitchRow("Новый вес указан примерно", estimated));
        page.AddFooter(Ui.Action("Сохранить", () => page.RunAsync(async () =>
        {
            decimal newWeight = Ui.Number(grams, "Вес", true);
            if (newWeight != entry.WeightInGrams || qualityChanged)
            {
                await _client.UpdateEntryAsync(profileId, entry.Id, entry.Revision, newWeight, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact);
            }

            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private static Entry AddNumber(FormPage page, string label, decimal? value = null)
    {
        Entry field = Ui.Field("", value is decimal number ? Ui.Amount(number) : null, true);
        page.Fields.Children.Add(Ui.Labeled(label, field));
        return field;
    }
}
