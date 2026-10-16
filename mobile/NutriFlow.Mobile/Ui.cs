using System.Globalization;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

internal static class Ui
{
    public static readonly Color Background = Color.FromArgb("#08151D");
    public static readonly Color Surface = Color.FromArgb("#112632");
    public static readonly Color SurfaceRaised = Color.FromArgb("#17333F");
    public static readonly Color SurfacePressed = Color.FromArgb("#214653");
    public static readonly Color TextPrimary = Color.FromArgb("#ECF8FF");
    public static readonly Color Muted = Color.FromArgb("#A7C2D0");
    public static readonly Color Accent = Color.FromArgb("#62D5EF");
    public static readonly Color AccentPressed = Color.FromArgb("#9AE8F8");
    public static readonly Color FieldStroke = Color.FromArgb("#5F879A");
    public static readonly Color FieldFocus = Color.FromArgb("#9AE8F8");
    public static readonly Color Protein = Color.FromArgb("#45A9F7");
    public static readonly Color Fat = Color.FromArgb("#F8C545");
    public static readonly Color Carbs = Color.FromArgb("#FF7184");
    public static readonly Color Green = Accent;

    public static Label Title(string text) => new()
    {
        Text = text,
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        TextColor = TextPrimary,
        FontAutoScalingEnabled = true,
        Margin = new Thickness(0, 8, 0, 2)
    };

    public static Label Text(string text, bool muted = false) => new()
    {
        Text = text,
        FontSize = 15,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = muted ? Muted : TextPrimary
    };

    public static Entry Field(string placeholder, string? value = null, bool number = false) => new InputEntry()
    {
        Placeholder = placeholder,
        Text = value,
        Keyboard = number ? Keyboard.Numeric : Keyboard.Default,
        BackgroundColor = SurfaceRaised,
        TextColor = TextPrimary,
        PlaceholderColor = Muted,
        FontSize = 16,
        FontAutoScalingEnabled = true,
        MinimumHeightRequest = 48,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };

    public static Button Action(string text, Func<Task> action, bool secondary = false)
    {
        Button button = new()
        {
            Text = text,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            FontAutoScalingEnabled = true,
            LineBreakMode = LineBreakMode.WordWrap,
            BackgroundColor = secondary ? SurfaceRaised : Accent,
            TextColor = secondary ? TextPrimary : Background,
            CornerRadius = 16,
            MinimumHeightRequest = 48,
            MinimumWidthRequest = 48,
            Padding = new Thickness(16, 12)
        };
        if (secondary)
        {
            ApplySecondaryStates(button);
        }

        button.Clicked += async (_, _) => await action();
        return button;
    }

    private static void ApplySecondaryStates(Button button)
    {
        VisualStateGroup states = new() { Name = "CommonStates" };
        states.States.Add(new VisualState { Name = "Normal" });
        states.States.Add(new VisualState
        {
            Name = "Pressed",
            Setters =
            {
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = SurfacePressed },
                new Setter { Property = Button.TextColorProperty, Value = TextPrimary }
            }
        });
        states.States.Add(new VisualState
        {
            Name = "Focused",
            Setters =
            {
                new Setter { Property = Button.BorderColorProperty, Value = Accent },
                new Setter { Property = Button.BorderWidthProperty, Value = 2d }
            }
        });
        states.States.Add(new VisualState
        {
            Name = "Disabled",
            Setters =
            {
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = SurfaceRaised },
                new Setter { Property = Button.TextColorProperty, Value = Muted }
            }
        });
        VisualStateManager.SetVisualStateGroups(button, new VisualStateGroupList { states });
    }

    public static Border Card(params View[] children)
    {
        VerticalStackLayout layout = new() { Spacing = 12 };
        foreach (View child in children)
        {
            layout.Children.Add(child);
        }

        return new Border
        {
            Content = layout,
            Padding = 20,
            BackgroundColor = Surface,
            Stroke = SurfaceRaised,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 }
        };
    }

    public static Grid Row(params View[] children)
    {
        Grid row = new() { ColumnSpacing = 8 };
        for (int index = 0; index < children.Length; index++)
        {
            View child = children[index];
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = child is Button or Label ? GridLength.Star : GridLength.Auto });
            row.Add(child, index, 0);
        }

        return row;
    }

    public static decimal Number(Entry field, string name, bool positive = false)
    {
        string value = (field.Text ?? "").Trim().Replace(',', '.');
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out decimal number) || number < 0 || (positive && number == 0))
        {
            throw new ArgumentException($"{name}: введите {(positive ? "положительное" : "неотрицательное")} число.");
        }

        return number;
    }

    public static string Name(Entry field, string label)
    {
        string value = (field.Text ?? "").Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException($"Заполните поле «{label}».");
        }

        return value;
    }

    public static string Amount(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    public static string Nutrition(NutritionResponse value) =>
        $"{Amount(value.Calories)} ккал · Б {Amount(value.ProteinGrams)} · Ж {Amount(value.FatGrams)} · У {Amount(value.CarbohydratesGrams)} г";

    public static string Nutrition(ProductResponse value) =>
        $"{Amount(value.Calories)} ккал · Б {Amount(value.ProteinGrams)} · Ж {Amount(value.FatGrams)} · У {Amount(value.CarbohydratesGrams)} г";

    public static string Quality(string? value) => value switch
    {
        "Exact" => "точные данные",
        "Verified" => "проверенные данные",
        "Estimated" => "оценочные данные",
        _ => "качество не установлено"
    };

    public static string Source(string value) => value switch
    {
        "ManualInput" => "ручной ввод",
        "LabelPhoto" => "фото этикетки",
        "ExternalService" => "внешний каталог",
        "SavedDish" => "сохранённое блюдо",
        "WebPage" => "веб-страница",
        _ => value
    };
}
