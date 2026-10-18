using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private Task EditGoalPageAsync(Guid profileId, DateOnly date, NutritionResponse? goal)
    {
        FormPage page = new("План");
        page.Fields.Children.Add(Ui.Text(date.ToString("d MMMM", DiaryCulture), true));

        Entry calories = Ui.Field("", (goal?.Calories ?? 2000m).ToString(CultureInfo.CurrentCulture), true);
        calories.AutomationId = "goal-calories";
        calories.FontSize = 30;
        calories.HorizontalTextAlignment = TextAlignment.Center;
        Label calorieLabel = Ui.Text("Калории, ккал");
        page.Fields.Children.Add(Ui.Card(calorieLabel, calories));

        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(goal?.ProteinGrams ?? 0m,
            goal?.FatGrams ?? 0m, goal?.CarbohydratesGrams ?? 0m);
        bool updating = false;
        List<(NutritionMacro Macro, string Name, Slider Slider, Label Value)> controls = [];
        VerticalStackLayout macros = new() { Spacing = 16 };
        Button save = Ui.Action("Сохранить", () => page.RunAsync(async () =>
        {
            NutritionValues values = allocation.ToNutrition(Ui.Number(calories, "Калории"));
            await _client.SetDailyGoalAsync(profileId, date,
                new NutritionResponse(values.Calories, values.ProteinGrams, values.FatGrams, values.CarbohydratesGrams));
            await Navigation.PopAsync();
        }));
        save.AutomationId = "goal-save";
        AddMacro(NutritionMacro.Protein, "Белки", Ui.Protein, "protein");
        AddMacro(NutritionMacro.Fat, "Жиры", Ui.Fat, "fat");
        AddMacro(NutritionMacro.Carbohydrates, "Углеводы", Ui.Carbs, "carbs");
        page.Fields.Children.Add(Ui.Card(macros));
        page.Fields.Children.Add(save);
        calories.TextChanged += (_, _) => Refresh();
        Refresh();
        return Navigation.PushAsync(page);

        void AddMacro(NutritionMacro macro, string name, Color color, string id)
        {
            Label title = Ui.Text(name);
            title.FontAttributes = FontAttributes.Bold;
            Label value = Ui.Text("");
            value.AutomationId = $"goal-{id}-value";
            value.HorizontalTextAlignment = TextAlignment.End;
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
                MaximumTrackColor = Ui.SurfacePressed,
                ThumbColor = color
            };
            SemanticProperties.SetHint(slider, "Доля калорий. Остальные два значения изменятся автоматически");
            slider.ValueChanged += (_, args) =>
            {
                if (!updating)
                {
                    allocation = allocation.WithShare(macro, (decimal)Math.Clamp(args.NewValue, 0, 100));
                    Refresh();
                }
            };
            controls.Add((macro, name, slider, value));
            VerticalStackLayout row = new() { Spacing = 4 };
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
                    control.Value.Text = valid ? $"{Ui.MacroAmount(grams)} г" : "—";
                    SemanticProperties.SetDescription(control.Slider,
                        valid ? $"{control.Name}. {Ui.MacroAmount(grams)} граммов" : control.Name);
                }
                save.IsEnabled = valid;
                save.BackgroundColor = valid ? Ui.Accent : Ui.SurfaceRaised;
                save.TextColor = valid ? Ui.Background : Ui.Muted;
            }
            finally
            {
                updating = false;
            }
        }
    }
}
