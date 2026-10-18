using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage : ContentPage
{
    private readonly LocalProfileStore _profiles;
    private readonly LocalMealClient _client;
    private readonly Button _profileButton;
    private readonly Button _addButton;
    private readonly VerticalStackLayout _body = new() { Padding = new Thickness(20, 0, 20, 28), Spacing = 16 };
    private readonly Grid _navigation = new() { ColumnSpacing = 4, Padding = new Thickness(8, 8, 8, 12) };
    private readonly ContentView _addMenu = new() { IsVisible = false, AutomationId = "add-menu", Margin = new Thickness(20, 0, 20, 4) };
    private readonly ScrollView _scroll;
    private readonly VerticalStackLayout _top = new() { Spacing = 0 };
    private readonly ContentView _welcome = new() { IsVisible = false };
    private readonly ActivityIndicator _activity = new() { Color = Ui.Accent, IsVisible = false, WidthRequest = 20, HeightRequest = 20 };
    private readonly Label _status = Ui.Text("", true);
    private LocalProfile? _profile;
    private MealSessionResponse? _session;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private MealSessionPurpose _purpose;
    private string _draftText = "";
    private string _tab = "Дневник";
    private bool _busy;
    private bool _scrollToPreview;

    public MainPage(LocalProfileStore profiles, LocalMealClient client)
    {
        InitializeComponent();
        _profiles = profiles;
        _client = client;
        Title = "NutriFlow";
        BackgroundColor = Ui.Background;
#if ANDROID
        SafeAreaEdges = SafeAreaEdges.All;
#endif
        NavigationPage.SetHasNavigationBar(this, false);
        _profileButton = Ui.Action("Профиль", OpenProfilesAsync, true);
        _profileButton.FontSize = 13;
        _profileButton.MaximumWidthRequest = 140;
        _profileButton.LineBreakMode = LineBreakMode.TailTruncation;
        _profileButton.AutomationId = "profile-switcher";
        _addButton = CreateAddButton();
        _addMenu.Content = CreateAddMenu();

        Label brand = Ui.Title("NutriFlow");
        brand.FontSize = 25;
        brand.Margin = 0;
        brand.CharacterSpacing = -0.8;
        brand.VerticalOptions = LayoutOptions.Center;
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } },
            ColumnSpacing = 12,
            Padding = new Thickness(22, 14, 20, 16)
        };
        header.Add(brand, 0);
        header.Add(_profileButton, 1);
        _status.IsVisible = false;
        _status.FontSize = 12;
        HorizontalStackLayout feedback = new() { Spacing = 8, Margin = new Thickness(22, 0, 20, 8) };
        feedback.Children.Add(_activity);
        feedback.Children.Add(_status);
        _top.Children.Add(header);
        _top.Children.Add(feedback);

        for (int index = 0; index < 3; index++)
        {
            _navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }

        _scroll = new ScrollView { Content = _body };
        VerticalStackLayout bottom = new() { Spacing = 0 };
        bottom.Children.Add(_addMenu);
        bottom.Children.Add(_navigation);
        Grid layout = new()
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, new() { Height = GridLength.Star }, new() { Height = GridLength.Auto }
            }
        };
        layout.Add(_top, 0, 0);
        layout.Add(_scroll, 0, 1);
        layout.Add(bottom, 0, 2);
        layout.Add(_welcome, 0, 0);
        Grid.SetRowSpan(_welcome, 3);
        Content = layout;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(async () =>
        {
            LocalProfile? selected = await _profiles.GetSelectedAsync();
            if (selected?.Id != _profile?.Id)
            {
                _profile = selected;
                _session = null;
                _draftText = "";
                if (selected is not null)
                {
                    await ResumeSessionAsync(selected.Id);
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
        _profileButton.Text = _profile?.Name ?? "Профиль";
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

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetAddMenuOpen(false);
        _profileButton.IsEnabled = false;
        _navigation.IsEnabled = false;
        _addMenu.IsEnabled = false;
        _body.IsEnabled = false;
        _welcome.IsEnabled = false;
        _activity.IsRunning = true;
        _activity.IsVisible = true;
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
            await DisplayAlertAsync("Не удалось выполнить действие", FormPage.ErrorText(exception), "Понятно");
        }
        finally
        {
            _activity.IsRunning = false;
            _activity.IsVisible = false;
            _profileButton.IsEnabled = true;
            _navigation.IsEnabled = true;
            _addMenu.IsEnabled = true;
            _body.IsEnabled = true;
            _welcome.IsEnabled = true;
            _busy = false;
        }
    }

    private Guid CurrentProfileId() => _profile?.Id ?? throw new InvalidOperationException("Сначала создайте или выберите профиль.");

    private async Task RenderAsync()
    {
        RenderNavigation();
        _body.Children.Clear();
        bool hasProfile = _profile is not null;
        _top.IsVisible = hasProfile;
        _scroll.IsVisible = hasProfile;
        _welcome.IsVisible = !hasProfile;
        if (_profile is null)
        {
            RenderWelcome();
            return;
        }

        _welcome.Content = null;
        Guid profileId = _profile.Id;
        switch (_tab)
        {
            case "Дневник": await RenderDiaryAsync(profileId); break;
            case "Ввод": RenderInput(profileId); break;
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

    private Task CreateProfilePageAsync()
    {
        FormPage page = new("Начало");
        Entry name = Ui.Field("Имя");
        name.AutomationId = "profile-name";
        SemanticProperties.SetDescription(name, "Твоё имя");
        page.Fields.Children.Add(Ui.Title("Твоё имя"));
        page.Fields.Children.Add(name);
        Button create = Ui.Action("Продолжить", () => page.RunAsync(async () =>
        {
            LocalProfile created = await _profiles.CreateAsync(Ui.Name(name, "Имя"));
            await _profiles.SelectAsync(created.Id);
            await Navigation.PopAsync();
        }));
        create.AutomationId = "profile-create";
        page.Fields.Children.Add(create);
        return Navigation.PushAsync(page);
    }

    private Task EditEntryPageAsync(Guid profileId, MealEntryResponse entry)
    {
        FormPage page = new("Изменить запись");
        page.Fields.Children.Add(Ui.Title(entry.Name));
        page.Fields.Children.Add(Ui.Text("КБЖУ пересчитаются по сохранённому составу."));
        Entry grams = AddNumber(page, "Вес порции, г", entry.WeightInGrams);
        Switch estimated = new();
        page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес указан примерно")));
        page.Fields.Children.Add(Ui.Action("Сохранить", () => page.RunAsync(async () =>
        {
            await _client.UpdateEntryAsync(profileId, entry.Id, entry.Revision, Ui.Number(grams, "Вес", true), estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact);
            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private static Entry AddNumber(FormPage page, string label, decimal? value = null)
    {
        page.Fields.Children.Add(Ui.Text(label));
        Entry field = Ui.Field("", value?.ToString(CultureInfo.CurrentCulture), true);
        page.Fields.Children.Add(field);
        return field;
    }
}
