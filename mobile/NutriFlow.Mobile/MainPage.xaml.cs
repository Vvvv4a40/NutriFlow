using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage : ContentPage
{
    private readonly LocalProfileStore _profiles;
    private readonly LocalMealClient _client;
    private readonly Picker _profilePicker = new() { Title = "Выберите профиль", ItemDisplayBinding = new Binding(nameof(LocalProfile.Name)) };
    private readonly Button _newProfile;
    private readonly VerticalStackLayout _body = new() { Padding = new Thickness(18, 8, 18, 24), Spacing = 12 };
    private readonly HorizontalStackLayout _navigation = new() { Spacing = 6, Padding = new Thickness(18, 0, 18, 4) };
    private readonly ActivityIndicator _activity = new() { Color = Ui.Green };
    private readonly Label _status = Ui.Text("", true);
    private LocalProfile? _profile;
    private MealSessionResponse? _session;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private MealSessionPurpose _purpose;
    private string _draftText = "";
    private string _tab = "Дневник";
    private bool _busy;
    private bool _changingProfiles;

    public MainPage(LocalProfileStore profiles, LocalMealClient client)
    {
        InitializeComponent();
        _profiles = profiles;
        _client = client;
        Title = "NutriFlow";
        _profilePicker.HorizontalOptions = LayoutOptions.Fill;
        _profilePicker.SelectedIndexChanged += async (_, _) =>
        {
            if (_changingProfiles || _busy || _profilePicker.SelectedItem is not LocalProfile selected || selected.Id == _profile?.Id)
            {
                return;
            }

            await RunAsync(async () =>
            {
                _profile = await _profiles.SelectAsync(selected.Id);
                _session = null;
                _draftText = "";
                await ResumeSessionAsync(selected.Id);
                await RenderAsync();
            });
        };
        _newProfile = Ui.Action("+ Профиль", CreateProfilePageAsync, true);
        Grid profileRow = new() { Padding = new Thickness(18, 8), ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        profileRow.Add(_profilePicker, 0);
        profileRow.Add(_newProfile, 1);
        foreach (string tab in new[] { "Дневник", "Ввод", "Продукты", "Блюда", "Настройки" })
        {
            _navigation.Children.Add(Ui.Action(tab, () => RunAsync(async () =>
            {
                _tab = tab;
                await RenderAsync();
            }), true));
        }

        VerticalStackLayout top = new() { Spacing = 0 };
        top.Children.Add(profileRow);
        top.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _navigation });
        top.Children.Add(_activity);
        _status.Margin = new Thickness(18, 0);
        top.Children.Add(_status);
        Grid layout = new() { RowDefinitions = new RowDefinitionCollection { new() { Height = GridLength.Auto }, new() { Height = GridLength.Star } } };
        layout.Add(top, 0, 0);
        layout.Add(new ScrollView { Content = _body }, 0, 1);
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
                _session = await _client.FindSessionAsync(_profile.Id, _session.Id);
            }

            await RefreshProfilesAsync();
            await RenderAsync();
        });
    }

    private async Task RefreshProfilesAsync()
    {
        _changingProfiles = true;
        try
        {
            _profilePicker.ItemsSource = (await _profiles.ListAsync()).ToList();
            _profilePicker.SelectedItem = _profilePicker.ItemsSource.Cast<LocalProfile>().FirstOrDefault(item => item.Id == _profile?.Id);
        }
        finally
        {
            _changingProfiles = false;
        }
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
        _profilePicker.IsEnabled = false;
        _newProfile.IsEnabled = false;
        _navigation.IsEnabled = false;
        _body.IsEnabled = false;
        _activity.IsRunning = true;
        _status.Text = "Выполняется…";
        try
        {
            await action();
            _status.Text = "";
        }
        catch (Exception exception)
        {
            _status.Text = "Действие не завершено";
            await DisplayAlertAsync("Не удалось выполнить действие", FormPage.ErrorText(exception), "Понятно");
        }
        finally
        {
            _activity.IsRunning = false;
            _profilePicker.IsEnabled = true;
            _newProfile.IsEnabled = true;
            _navigation.IsEnabled = true;
            _body.IsEnabled = true;
            _busy = false;
        }
    }

    private Guid CurrentProfileId() => _profile?.Id ?? throw new InvalidOperationException("Сначала создайте или выберите профиль.");

    private async Task RenderAsync()
    {
        _body.Children.Clear();
        if (_profile is null)
        {
            _body.Children.Add(Ui.Title("Питание под вашим контролем"));
            _body.Children.Add(Ui.Text("Создайте локальный профиль. Продукты, блюда, дневник и цели сохраняются на этом телефоне."));
            _body.Children.Add(Ui.Action("Создать профиль", CreateProfilePageAsync));
            _body.Children.Add(Ui.Text("Ручной ввод работает без интернета. Для описания еды, фото этикеток и голоса можно добавить личный ключ Groq.", true));
            return;
        }

        Guid profileId = _profile.Id;
        switch (_tab)
        {
            case "Дневник": await RenderDiaryAsync(profileId); break;
            case "Ввод": RenderInput(profileId); break;
            case "Продукты": await RenderProductsAsync(profileId); break;
            case "Блюда": await RenderDishesAsync(profileId); break;
            case "Настройки": await RenderSettingsAsync(profileId); break;
        }
    }

    private Task CreateProfilePageAsync()
    {
        FormPage page = new("Новый профиль");
        Entry name = Ui.Field("Имя профиля");
        page.Fields.Children.Add(Ui.Title("Локальный профиль"));
        page.Fields.Children.Add(Ui.Text("Данные каждого профиля хранятся отдельно на этом телефоне."));
        page.Fields.Children.Add(name);
        page.Fields.Children.Add(Ui.Action("Создать", () => page.RunAsync(async () =>
        {
            LocalProfile created = await _profiles.CreateAsync(Ui.Name(name, "Имя профиля"));
            await _profiles.SelectAsync(created.Id);
            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private async Task RenderDiaryAsync(Guid profileId)
    {
        _body.Children.Add(Ui.Title("Дневник питания"));
        DatePicker date = new() { Date = _date.ToDateTime(TimeOnly.MinValue), Format = "dd.MM.yyyy" };
        date.DateSelected += async (_, _) => await RunAsync(async () =>
        {
            _date = DateOnly.FromDateTime(date.Date ?? DateTime.Today);
            await RenderDiaryRefreshAsync(profileId);
        });
        _body.Children.Add(date);
        DailyProgressResponse progress = await _client.GetDailyProgressAsync(profileId, _date);
        _body.Children.Add(Ui.Card(Ui.Text("Съедено за день"), Ui.Title($"{Ui.Amount(progress.Consumed.Calories)} ккал"), Ui.Text(Ui.Nutrition(progress.Consumed))));
        if (progress.Goal is not null)
        {
            _body.Children.Add(Ui.Card(Ui.Text("Дневная цель"), Ui.Text(Ui.Nutrition(progress.Goal)),
                GoalProgress("Калории", progress.Consumed.Calories, progress.Goal.Calories, "ккал"),
                GoalProgress("Белки", progress.Consumed.ProteinGrams, progress.Goal.ProteinGrams, "г"),
                GoalProgress("Жиры", progress.Consumed.FatGrams, progress.Goal.FatGrams, "г"),
                GoalProgress("Углеводы", progress.Consumed.CarbohydratesGrams, progress.Goal.CarbohydratesGrams, "г")));
        }
        else
        {
            _body.Children.Add(Ui.Text("Установите цель, чтобы видеть прогресс по КБЖУ.", true));
        }

        DateOnly selectedDate = _date;
        _body.Children.Add(Ui.Action("Настроить цель", () => EditGoalPageAsync(profileId, selectedDate, progress.Goal), true));
        _body.Children.Add(Ui.Action("Добавить еду", () => RunAsync(async () => { _tab = "Ввод"; await RenderAsync(); })));
        if (progress.Entries.Count == 0)
        {
            _body.Children.Add(Ui.Text("За этот день пока нет записей.", true));
        }

        foreach (MealEntryResponse entry in progress.Entries)
        {
            _body.Children.Add(Ui.Card(Ui.Text(entry.Name), Ui.Text($"{Ui.Amount(entry.WeightInGrams)} г · {Ui.Quality(entry.Quality)}", true),
                Ui.Text(Ui.Nutrition(entry.Nutrition)),
                Ui.Row(Ui.Action("Изменить вес", () => EditEntryPageAsync(profileId, entry), true),
                    Ui.Action("Удалить", () => RunAsync(async () =>
                    {
                        if (await DisplayAlertAsync("Удалить запись?", $"{entry.Name}, {Ui.Amount(entry.WeightInGrams)} г", "Удалить", "Отмена"))
                        {
                            await _client.DeleteEntryAsync(profileId, entry.Id, entry.Revision);
                            await RenderAsync();
                        }
                    }), true))));
        }
    }

    private Task RenderDiaryRefreshAsync(Guid profileId)
    {
        _body.Children.Clear();
        return RenderDiaryAsync(profileId);
    }

    private static View GoalProgress(string name, decimal consumed, decimal goal, string unit)
    {
        VerticalStackLayout row = new() { Spacing = 4 };
        row.Children.Add(Ui.Text($"{name}: {Ui.Amount(consumed)} / {Ui.Amount(goal)} {unit}", true));
        row.Children.Add(new ProgressBar { Progress = goal > 0 ? (double)Math.Min(consumed / goal, 1m) : 0, ProgressColor = Ui.Green });
        if (consumed > goal)
        {
            row.Children.Add(Ui.Text($"Превышение: {Ui.Amount(consumed - goal)} {unit}", true));
        }

        return row;
    }

    private Task EditGoalPageAsync(Guid profileId, DateOnly date, NutritionResponse? goal)
    {
        FormPage page = new("Цель на день");
        page.Fields.Children.Add(Ui.Text(date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
        Entry calories = AddNumber(page, "Калории, ккал", goal?.Calories);
        Entry protein = AddNumber(page, "Белки, г", goal?.ProteinGrams);
        Entry fat = AddNumber(page, "Жиры, г", goal?.FatGrams);
        Entry carbs = AddNumber(page, "Углеводы, г", goal?.CarbohydratesGrams);
        page.Fields.Children.Add(Ui.Action("Сохранить цель", () => page.RunAsync(async () =>
        {
            await _client.SetDailyGoalAsync(profileId, date, new NutritionResponse(Ui.Number(calories, "Калории"), Ui.Number(protein, "Белки"), Ui.Number(fat, "Жиры"), Ui.Number(carbs, "Углеводы")));
            await Navigation.PopAsync();
        })));
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
        Entry field = Ui.Field(label, value?.ToString(CultureInfo.CurrentCulture), true);
        page.Fields.Children.Add(field);
        return field;
    }
}
