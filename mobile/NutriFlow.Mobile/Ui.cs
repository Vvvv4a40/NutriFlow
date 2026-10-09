using System.Globalization;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

internal static class Ui
{
    public static readonly Color Green = Color.FromArgb("#185B45");
    public static readonly Color Muted = Color.FromArgb("#63776D");

    public static Label Title(string text) => new()
    {
        Text = text,
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 8, 0, 2)
    };

    public static Label Text(string text, bool muted = false) => new()
    {
        Text = text,
        FontSize = 15,
        TextColor = muted ? Muted : Color.FromArgb("#18372C")
    };

    public static Entry Field(string placeholder, string? value = null, bool number = false) => new()
    {
        Placeholder = placeholder,
        Text = value,
        Keyboard = number ? Keyboard.Numeric : Keyboard.Default,
        BackgroundColor = Colors.White,
        TextColor = Color.FromArgb("#18372C"),
        PlaceholderColor = Muted,
        MinimumHeightRequest = 48,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };

    public static Button Action(string text, Func<Task> action, bool secondary = false)
    {
        Button button = new()
        {
            Text = text,
            FontSize = 14,
            BackgroundColor = secondary ? Color.FromArgb("#E5EEE8") : Green,
            TextColor = secondary ? Green : Colors.White,
            CornerRadius = 12,
            MinimumHeightRequest = 46,
            Padding = new Thickness(12, 8)
        };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    public static Border Card(params View[] children)
    {
        VerticalStackLayout layout = new() { Spacing = 10 };
        foreach (View child in children)
        {
            layout.Children.Add(child);
        }

        return new Border
        {
            Content = layout,
            Padding = 16,
            BackgroundColor = Colors.White,
            Stroke = Color.FromArgb("#DAE5DD"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 }
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
