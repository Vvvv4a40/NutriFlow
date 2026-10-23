using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private static readonly (string Name, decimal Protein, decimal Fat, decimal Carbohydrates)[] GoalPresets =
    [
        ("Баланс", 30m, 30m, 40m),
        ("Больше белка", 40m, 30m, 30m),
        ("Меньше углеводов", 35m, 45m, 20m)
    ];

    private Task EditGoalPageAsync(Guid profileId, DateOnly date, NutritionResponse? goal)
    {
        FormPage page = new("Цель на день");
        page.Fields.Children.Add(Ui.Caption($"Цель для дневника за {date.ToString("d MMMM", DiaryCulture)}"));

        Entry calories = Ui.Field("2000", Ui.Amount(goal?.Calories ?? 2000m), true);
        calories.AutomationId = "goal-calories";
        calories.FontSize = 34;
        calories.FontAttributes = FontAttributes.Bold;
        calories.HorizontalTextAlignment = TextAlignment.Center;
        calories.MinimumHeightRequest = 72;
        SemanticProperties.SetDescription(calories, "Калории в день");
        Label unit = Ui.Caption("ккал в день");
        unit.HorizontalTextAlignment = TextAlignment.Center;
        page.Fields.Children.Add(Ui.Card(Ui.Overline("Калории"), calories, unit));

        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(goal?.ProteinGrams ?? 0m,
            goal?.FatGrams ?? 0m, goal?.CarbohydratesGrams ?? 0m);
        bool updating = false;
        List<(NutritionMacro Macro, string Name, Slider Slider, Label Value)> controls = [];
        VerticalStackLayout macros = new() { Spacing = 18 };
        Button save = Ui.Action("Сохранить цель", () => page.RunAsync(async () =>
        {
            NutritionValues values = allocation.ToNutrition(Ui.Number(calories, "Калории"));
            await _client.SetDailyGoalAsync(profileId, date,
                new NutritionResponse(values.Calories, values.ProteinGrams, values.FatGrams, values.CarbohydratesGrams));
            await Navigation.PopAsync();
        }));
        save.AutomationId = "goal-save";

        HorizontalStackLayout presets = new() { Spacing = 8 };
        string? selectedPreset = null;
        void RenderPresets()
        {
            presets.Children.Clear();
            foreach ((string name, decimal protein, decimal fat, decimal carbohydrates) in GoalPresets)
            {
                presets.Children.Add(Ui.Chip(name, name == selectedPreset, () =>
                {
                    selectedPreset = name;
                    allocation = MacroGoalAllocation.FromGrams(protein / 4m, fat / 9m, carbohydrates / 4m);
                    RenderPresets();
                    Refresh();
                    return Task.CompletedTask;
                }, $"{name}: белки {protein}, жиры {fat}, углеводы {carbohydrates} процентов калорий"));
            }
        }

        RenderPresets();

        AddMacro(NutritionMacro.Protein, "Белки", Ui.Protein, "protein");
        AddMacro(NutritionMacro.Fat, "Жиры", Ui.Fat, "fat");
        AddMacro(NutritionMacro.Carbohydrates, "Углеводы", Ui.Carbs, "carbs");
        page.Fields.Children.Add(Ui.Overline("Баланс БЖУ"));
        page.Fields.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = presets });
        page.Fields.Children.Add(Ui.Card(macros, Ui.Caption("Доли калорий связаны: когда меняете одну, две другие подстраиваются.")));
        page.AddFooter(save);
        calories.TextChanged += (_, _) => Refresh();
        Refresh();
        return Navigation.PushAsync(page);

        void AddMacro(NutritionMacro macro, string name, Color color, string id)
        {
            Label title = Ui.Strong(name, 15);
            title.TextColor = color;
            Label value = new() { FontSize = 15, FontAutoScalingEnabled = true, HorizontalTextAlignment = TextAlignment.End, FormattedText = new FormattedString() };
            value.AutomationId = $"goal-{id}-value";
            Grid caption = new()
            {
                ColumnSpacing = 8,
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }
                }
            };
            caption.Add(title, 0);
            caption.Add(value, 1);
            Slider slider = new()
            {
                AutomationId = $"goal-{id}",
                Minimum = 0,
                Maximum = 100,
                MinimumHeightRequest = 48,
                MinimumTrackColor = color,
                MaximumTrackColor = Ui.FieldStroke.WithAlpha(0.4f),
                ThumbColor = color
            };
            SemanticProperties.SetHint(slider, "Доля калорий. Остальные два значения изменятся автоматически");
            slider.ValueChanged += (_, args) =>
            {
                if (!updating)
                {
                    allocation = allocation.WithShare(macro, (decimal)Math.Clamp(args.NewValue, 0, 100));
                    if (selectedPreset is not null)
                    {
                        selectedPreset = null;
                        RenderPresets();
                    }

                    Refresh();
                }
            };
            controls.Add((macro, name, slider, value));
            VerticalStackLayout row = new() { Spacing = 2 };
            row.Children.Add(caption);
            row.Children.Add(slider);
            macros.Children.Add(row);
        }

        void Refresh()
        {
            updating = true;
            try
            {
                bool valid = decimal.TryParse((calories.Text ?? "").Trim().Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out decimal total) && total >= 0;
                NutritionValues values = allocation.ToNutrition(valid ? total : 0m);
                foreach (var control in controls)
                {
                    (decimal percent, decimal grams) = control.Macro switch
                    {
                        NutritionMacro.Protein => (allocation.ProteinPercent, values.ProteinGrams),
                        NutritionMacro.Fat => (allocation.FatPercent, values.FatGrams),
                        _ => (allocation.CarbohydratesPercent, values.CarbohydratesGrams)
                    };
                    control.Slider.Value = (double)percent;
                    control.Value.FormattedText.Spans.Clear();
                    control.Value.FormattedText.Spans.Add(new Span { Text = valid ? $"{Ui.MacroAmount(grams)} г" : "—", TextColor = Ui.TextPrimary, FontFamily = Ui.MediumFont });
                    control.Value.FormattedText.Spans.Add(new Span { Text = $"  {percent.ToString("0", CultureInfo.CurrentCulture)}%", TextColor = Ui.Subtle });
                    SemanticProperties.SetDescription(control.Slider,
                        valid ? $"{control.Name}. {Ui.MacroAmount(grams)} граммов" : control.Name);
                }

                Ui.SetEnabled(save, valid);
            }
            finally
            {
                updating = false;
            }
        }
    }
}
