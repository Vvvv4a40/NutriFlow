using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

internal static class Ui
{
    // "Terracotta & Linen": linen and terracotta in the light theme, espresso and lighter terracotta in the dark one
    private sealed record Palette(
        string Background, string Surface, string SurfaceRaised, string SurfacePressed, string Stroke,
        string TextPrimary, string Muted, string Subtle,
        string Accent, string AccentPressed, string AccentSoft, string OnAccent,
        string FieldStroke, string FieldFocus,
        string Protein, string Fat, string Carbs,
        string Success, string Warning, string Danger);

    private static readonly Palette LightPalette = new(
        "#F5EEE4", "#FFFBF5", "#EFE6D9", "#E3D6C4", "#E6DBCC",
        "#2D211B", "#6E5F53", "#756458",
        "#A2461F", "#8A3A18", "#F7E7DC", "#FFFFFF",
        "#8C7766", "#A2461F",
        "#3F6286", "#946C1C", "#6E5A8E",
        "#2E7A4E", "#8A5200", "#A8283E");

    private static readonly Palette DarkPalette = new(
        "#151110", "#201A17", "#2F2722", "#3C322B", "#2C2420",
        "#F4EBE1", "#BBAA9A", "#9C8B7D",
        "#E5824F", "#F0A57C", "#36231A", "#22100A",
        "#8A7667", "#F0A57C",
        "#8FB0D3", "#DDB866", "#B3A2D6",
        "#8FD3A8", "#F0B65E", "#F0899A");

    // screens read the current theme while they are built and are rebuilt when the system theme changes
    public static bool IsDark => Application.Current?.RequestedTheme != AppTheme.Light;

    private static Palette Current => IsDark ? DarkPalette : LightPalette;

    public static Color Background => Color.FromArgb(Current.Background);
    public static Color Surface => Color.FromArgb(Current.Surface);
    public static Color SurfaceRaised => Color.FromArgb(Current.SurfaceRaised);
    public static Color SurfacePressed => Color.FromArgb(Current.SurfacePressed);
    public static Color Stroke => Color.FromArgb(Current.Stroke);
    public static Color TextPrimary => Color.FromArgb(Current.TextPrimary);
    public static Color Muted => Color.FromArgb(Current.Muted);
    public static Color Subtle => Color.FromArgb(Current.Subtle);
    public static Color Accent => Color.FromArgb(Current.Accent);
    public static Color AccentPressed => Color.FromArgb(Current.AccentPressed);
    public static Color AccentSoft => Color.FromArgb(Current.AccentSoft);
    public static Color OnAccent => Color.FromArgb(Current.OnAccent);
    public static Color FieldStroke => Color.FromArgb(Current.FieldStroke);
    public static Color FieldFocus => Color.FromArgb(Current.FieldFocus);
    public static Color Protein => Color.FromArgb(Current.Protein);
    public static Color Fat => Color.FromArgb(Current.Fat);
    public static Color Carbs => Color.FromArgb(Current.Carbs);
    public static Color Success => Color.FromArgb(Current.Success);
    public static Color Warning => Color.FromArgb(Current.Warning);
    public static Color Danger => Color.FromArgb(Current.Danger);

    public const string MediumFont = "sans-serif-medium";
    public const double ScreenPadding = 20;
    public const double CardRadius = 22;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static Label Title(string text)
    {
        Label label = new()
        {
            Text = text,
            FontSize = 28,
            FontAttributes = FontAttributes.Bold,
            TextColor = TextPrimary,
            CharacterSpacing = -0.6,
            FontAutoScalingEnabled = true,
            LineBreakMode = LineBreakMode.WordWrap
        };
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level1);
        return label;
    }

    public static Label Headline(string text, double size = 20)
    {
        Label label = new()
        {
            Text = text,
            FontSize = size,
            FontAttributes = FontAttributes.Bold,
            TextColor = TextPrimary,
            FontAutoScalingEnabled = true,
            LineBreakMode = LineBreakMode.WordWrap
        };
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return label;
    }

    public static Label Text(string text, bool muted = false) => new()
    {
        Text = text,
        FontSize = 15,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = muted ? Muted : TextPrimary
    };

    public static Label Strong(string text, double size = 16) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = MediumFont,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = TextPrimary
    };

    public static Label Caption(string text, Color? color = null) => new()
    {
        Text = text,
        FontSize = 13,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = color ?? Muted
    };

    public static Label Overline(string text)
    {
        Label label = new()
        {
            Text = text.ToUpper(Russian),
            FontSize = 12,
            FontFamily = MediumFont,
            CharacterSpacing = 1.2,
            FontAutoScalingEnabled = true,
            TextColor = Subtle
        };
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return label;
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontFamily = MediumFont,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = Muted
    };

    public static Entry Field(string placeholder, string? value = null, bool number = false) => new InputEntry()
    {
        Placeholder = placeholder,
        Text = value,
        Keyboard = number ? Keyboard.Numeric : Keyboard.Default,
        BackgroundColor = SurfaceRaised,
        TextColor = TextPrimary,
        PlaceholderColor = Subtle,
        FontSize = 16,
        FontAutoScalingEnabled = true,
        MinimumHeightRequest = 52,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };

    public static View Labeled(string label, View field)
    {
        VerticalStackLayout stack = new() { Spacing = 8 };
        stack.Children.Add(FieldLabel(label));
        stack.Children.Add(field);
        if (field is not Layout && string.IsNullOrEmpty(SemanticProperties.GetDescription(field)))
        {
            SemanticProperties.SetDescription(field, label);
        }

        return stack;
    }

    public static Button Action(string text, Func<Task> action, bool secondary = false)
    {
        Button button = new()
        {
            Text = text,
            FontSize = 16,
            FontFamily = MediumFont,
            FontAutoScalingEnabled = true,
            LineBreakMode = LineBreakMode.WordWrap,
            BackgroundColor = secondary ? SurfaceRaised : Accent,
            TextColor = secondary ? TextPrimary : OnAccent,
            CornerRadius = 16,
            MinimumHeightRequest = 52,
            MinimumWidthRequest = 48,
            Padding = new Thickness(18, 12)
        };
        if (secondary)
        {
            ApplyStates(button, SurfacePressed, TextPrimary);
        }

        OnClick(button, action);
        return button;
    }

    // a second tap while the first action is still running would push the same page twice
    private static void OnClick(Button button, Func<Task> action)
    {
        bool running = false;
        button.Clicked += async (_, _) =>
        {
            if (running)
            {
                return;
            }

            running = true;
            try
            {
                await action();
            }
            finally
            {
                running = false;
            }
        };
    }

    public static Button Ghost(string text, Func<Task> action, Color? color = null)
    {
        Button button = Action(text, action, true);
        button.BackgroundColor = Colors.Transparent;
        button.TextColor = color ?? Accent;
        button.Padding = new Thickness(12, 10);
        button.MinimumHeightRequest = 48;
        ApplyStates(button, SurfaceRaised, color ?? Accent);
        return button;
    }

    public static void SetEnabled(Button button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.BackgroundColor = enabled ? Accent : SurfaceRaised;
        button.TextColor = enabled ? OnAccent : Subtle;
    }

    public static void ApplyStates(Button button, Color pressed, Color text)
    {
        VisualStateGroup states = new() { Name = "CommonStates" };
        states.States.Add(new VisualState { Name = "Normal" });
        states.States.Add(new VisualState
        {
            Name = "Pressed",
            Setters =
            {
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = pressed },
                new Setter { Property = Button.TextColorProperty, Value = text }
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
                new Setter { Property = Button.TextColorProperty, Value = Subtle }
            }
        });
        VisualStateManager.SetVisualStateGroups(button, new VisualStateGroupList { states });
    }

    // a transparent button under custom content keeps one accessible, pressable target per row
    public static Grid Pressable(View content, Func<Task> action, string description, string? hint = null, string? automationId = null)
    {
        Button layer = new()
        {
            BackgroundColor = Colors.Transparent,
            CornerRadius = 0,
            Padding = 0,
            MinimumHeightRequest = 48,
            BorderWidth = 0
        };
        ApplyStates(layer, SurfacePressed, TextPrimary);
        if (automationId is not null)
        {
            layer.AutomationId = automationId;
        }

        SemanticProperties.SetDescription(layer, description);
        if (hint is not null)
        {
            SemanticProperties.SetHint(layer, hint);
        }

        OnClick(layer, action);
        content.InputTransparent = true;
        content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Grid grid = new();
        grid.Add(layer);
        grid.Add(content);
        return grid;
    }

    public static Grid IconButton(AppIconKind icon, string description, Func<Task> action, Color? color = null, double size = 48)
    {
        Grid control = Pressable(new AppIcon(icon, color ?? Muted) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            action, description);
        ((Button)control.Children[0]).CornerRadius = (int)(size / 2);
        control.WidthRequest = size;
        control.HeightRequest = size;
        return control;
    }

    public static Border Card(params View[] children)
    {
        VerticalStackLayout layout = new() { Spacing = 12 };
        foreach (View child in children)
        {
            layout.Children.Add(child);
        }

        return Panel(layout, 18);
    }

    public static Border Panel(View content, double padding = 0) => new()
    {
        Content = content,
        Padding = padding,
        BackgroundColor = Ui.Surface,
        Stroke = Stroke,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = CardRadius }
    };

    // rows separated by hairlines inside one card read as a single list
    public static Border List(IEnumerable<View> rows)
    {
        VerticalStackLayout stack = new() { Spacing = 0 };
        foreach (View row in rows)
        {
            if (stack.Children.Count > 0)
            {
                stack.Children.Add(Divider(16));
            }

            stack.Children.Add(row);
        }

        return Panel(stack);
    }

    public static BoxView Divider(double inset = 0) => new()
    {
        HeightRequest = 1,
        Color = Stroke,
        Margin = new Thickness(inset, 0)
    };

    public static View Row(string title, string? subtitle, string? trailing, string? trailingCaption, Func<Task> action,
        string description, AppIconKind? icon = null, FormattedString? subtitleSpans = null, string? automationId = null,
        string? hint = null)
    {
        Grid content = new()
        {
            Padding = new Thickness(16, 14),
            ColumnSpacing = 14,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }
            }
        };
        if (icon is AppIconKind kind)
        {
            content.Add(IconTile(kind, Accent), 0);
        }
        else
        {
            content.ColumnDefinitions[0].Width = new GridLength(0);
            content.Padding = new Thickness(2, 14, 16, 14);
        }

        VerticalStackLayout text = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        text.Children.Add(Strong(title));
        if (subtitleSpans is not null)
        {
            text.Children.Add(new Label { FormattedText = subtitleSpans, FontSize = 13, FontAutoScalingEnabled = true, LineBreakMode = LineBreakMode.WordWrap });
        }
        else if (!string.IsNullOrEmpty(subtitle))
        {
            text.Children.Add(Caption(subtitle));
        }

        content.Add(text, 1);
        if (trailing is not null || trailingCaption is not null)
        {
            VerticalStackLayout end = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            if (trailing is not null)
            {
                Label value = Strong(trailing);
                value.HorizontalTextAlignment = TextAlignment.End;
                end.Children.Add(value);
            }

            if (trailingCaption is not null)
            {
                Label caption = Caption(trailingCaption);
                caption.FontSize = 12;
                caption.HorizontalTextAlignment = TextAlignment.End;
                end.Children.Add(caption);
            }

            content.Add(end, 2);
        }
        else
        {
            content.Add(new AppIcon(AppIconKind.Next, Subtle, 18) { VerticalOptions = LayoutOptions.Center }, 2);
        }

        return Pressable(content, action, description, hint, automationId);
    }

    public static Border IconTile(AppIconKind icon, Color color, double size = 40) => new()
    {
        WidthRequest = size,
        HeightRequest = size,
        BackgroundColor = color.WithAlpha(0.14f),
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = size * 0.32 },
        VerticalOptions = LayoutOptions.Center,
        Content = new AppIcon(icon, color, size * 0.55) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
    };

    public static Border Badge(string text, Color color) => new()
    {
        BackgroundColor = color.WithAlpha(0.15f),
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 10 },
        Padding = new Thickness(9, 3),
        HorizontalOptions = LayoutOptions.Start,
        VerticalOptions = LayoutOptions.Center,
        Content = new Label
        {
            Text = text,
            FontSize = 12,
            FontFamily = MediumFont,
            TextColor = color,
            FontAutoScalingEnabled = true
        }
    };

    public static View? QualityBadge(string? quality) => quality switch
    {
        "Exact" or "Verified" => null,
        "Estimated" => Badge("≈ оценка", Warning),
        _ => Badge("не проверено", Muted)
    };

    public static Border ProgressBar(decimal value, decimal? maximum, Color color, double height = 6)
    {
        double fraction = maximum is > 0m ? (double)Math.Clamp(value / maximum.Value, 0m, 1m) : 0;
        Grid track = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = new GridLength(fraction, GridUnitType.Star) },
                new() { Width = new GridLength(1 - fraction, GridUnitType.Star) }
            }
        };
        track.Add(new BoxView { Color = color, HeightRequest = height }, 0);
        return new Border
        {
            Content = track,
            HeightRequest = height,
            BackgroundColor = SurfacePressed,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = height / 2 }
        };
    }

    public static View Segmented(IReadOnlyList<(string Text, string AutomationId)> options, int selected, Action<int> select, bool enabled = true)
    {
        Grid choices = new() { ColumnSpacing = 4 };
        for (int index = 0; index < options.Count; index++)
        {
            int current = index;
            bool active = index == selected;
            Button option = new()
            {
                Text = options[index].Text,
                AutomationId = options[index].AutomationId,
                FontSize = 15,
                FontFamily = MediumFont,
                FontAutoScalingEnabled = true,
                CornerRadius = 12,
                Padding = new Thickness(8, 10),
                MinimumHeightRequest = 48,
                BackgroundColor = active ? SurfacePressed : Colors.Transparent,
                TextColor = active ? TextPrimary : Muted,
                IsEnabled = enabled
            };
            ApplyStates(option, active ? SurfacePressed : SurfaceRaised, active ? TextPrimary : Muted);
            SemanticProperties.SetDescription(option, active ? $"{option.Text}. Выбрано" : option.Text);
            option.Clicked += (_, _) => select(current);
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            choices.Add(option, index);
        }

        return new Border
        {
            Content = choices,
            BackgroundColor = Surface,
            Stroke = Stroke,
            StrokeThickness = 1,
            Padding = 4,
            StrokeShape = new RoundRectangle { CornerRadius = 16 }
        };
    }

    public static Button Chip(string text, bool selected, Func<Task> action, string? description = null)
    {
        Button chip = new()
        {
            Text = text,
            FontSize = 14,
            FontFamily = MediumFont,
            FontAutoScalingEnabled = true,
            TextColor = selected ? Accent : TextPrimary,
            BackgroundColor = selected ? AccentSoft : SurfaceRaised,
            BorderColor = selected ? Accent.WithAlpha(0.6f) : Colors.Transparent,
            BorderWidth = 1,
            CornerRadius = 24,
            Padding = new Thickness(16, 10),
            MinimumHeightRequest = 48
        };
        ApplyStates(chip, SurfacePressed, selected ? Accent : TextPrimary);
        string name = description ?? text;
        SemanticProperties.SetDescription(chip, selected ? $"{name}. Выбрано" : name);
        OnClick(chip, action);
        return chip;
    }

    public static View PickerField(Picker picker)
    {
        Grid field = new();
        field.Add(picker);
        field.Add(new AppIcon(AppIconKind.Expand, Subtle, 18)
        {
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 16, 0)
        });
        return field;
    }

    // opaque content would hide the pressed state drawn on the layer, so the fill moves to the layer itself
    public static void FillPressable(Grid pressable, Border content, Color fill)
    {
        Button layer = (Button)pressable.Children[0];
        layer.BackgroundColor = fill;
        if (content.StrokeShape is RoundRectangle shape)
        {
            layer.CornerRadius = (int)shape.CornerRadius.TopLeft;
        }

        content.BackgroundColor = Colors.Transparent;
        pressable.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VisualElement.IsEnabled))
            {
                pressable.Opacity = pressable.IsEnabled ? 1 : 0.45;
            }
        };
    }

    public static View SwitchRow(string text, Switch toggle, string? hint = null)
    {
        toggle.OnColor = AccentSoft;
        toggle.ThumbColor = toggle.IsToggled ? Accent : Muted;
        toggle.Toggled += (_, args) => toggle.ThumbColor = args.Value ? Accent : Muted;
        toggle.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(toggle, text);
        VerticalStackLayout labels = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        labels.Children.Add(Text(text));
        if (hint is not null)
        {
            labels.Children.Add(Caption(hint));
        }

        Grid row = new()
        {
            ColumnSpacing = 12,
            MinimumHeightRequest = 48,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        row.Add(labels, 0);
        row.Add(toggle, 1);
        TapGestureRecognizer tap = new();
        tap.Tapped += (_, _) => toggle.IsToggled = !toggle.IsToggled;
        labels.GestureRecognizers.Add(tap);
        return row;
    }

    public static View Expander(string title, View content, string? automationId = null)
    {
        content.IsVisible = false;
        Label caption = Strong(title, 15);
        caption.TextColor = Muted;
        AppIcon chevron = new(AppIconKind.Expand, Subtle, 18) { VerticalOptions = LayoutOptions.Center };
        Grid header = new()
        {
            Padding = new Thickness(0, 8),
            ColumnSpacing = 8,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        header.Add(caption, 0);
        header.Add(chevron, 1);
        Button? layer = null;
        Grid toggle = Pressable(header, () =>
        {
            content.IsVisible = !content.IsVisible;
            chevron.Rotation = content.IsVisible ? 180 : 0;
            SemanticProperties.SetDescription(layer!, content.IsVisible ? $"{title}, развёрнуто" : $"{title}, свёрнуто");
            return Task.CompletedTask;
        }, $"{title}, свёрнуто", "Показать или скрыть подробности", automationId);
        layer = (Button)toggle.Children[0];
        VerticalStackLayout stack = new() { Spacing = 8 };
        stack.Children.Add(toggle);
        stack.Children.Add(content);
        return stack;
    }

    public static View EmptyState(AppIconKind icon, string title, string message, View? action = null, string? automationId = null)
    {
        VerticalStackLayout stack = new() { Spacing = 10, Padding = new Thickness(8, 12) };
        Border tile = IconTile(icon, Accent, 56);
        tile.HorizontalOptions = LayoutOptions.Center;
        stack.Children.Add(tile);
        Label heading = Headline(title, 19);
        heading.HorizontalTextAlignment = TextAlignment.Center;
        stack.Children.Add(heading);
        Label body = Caption(message);
        body.FontSize = 14;
        body.HorizontalTextAlignment = TextAlignment.Center;
        stack.Children.Add(body);
        if (action is not null)
        {
            action.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(action);
        }

        Border card = Card(stack);
        if (automationId is not null)
        {
            card.AutomationId = automationId;
        }

        return card;
    }

    public static FormattedString MacroSpans(decimal protein, decimal fat, decimal carbs)
    {
        FormattedString text = new();
        text.Spans.Add(new Span { Text = $"Б {MacroAmount(protein)}", TextColor = Protein });
        text.Spans.Add(new Span { Text = "  ·  ", TextColor = Subtle });
        text.Spans.Add(new Span { Text = $"Ж {MacroAmount(fat)}", TextColor = Fat });
        text.Spans.Add(new Span { Text = "  ·  ", TextColor = Subtle });
        text.Spans.Add(new Span { Text = $"У {MacroAmount(carbs)} г", TextColor = Carbs });
        return text;
    }

    public static FormattedString MacroSpans(NutritionResponse value) =>
        MacroSpans(value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static FormattedString MacroSpans(ProductResponse value) =>
        MacroSpans(value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static Label MacroLine(NutritionResponse value, double size = 13) => new()
    {
        FormattedText = MacroSpans(value),
        FontSize = size,
        FontAutoScalingEnabled = true,
        LineBreakMode = LineBreakMode.WordWrap
    };

    // calories, then the macro breakdown, as one block for summaries
    public static View NutritionBlock(NutritionResponse? value, string? caption = null, double size = 28)
    {
        VerticalStackLayout block = new() { Spacing = 4 };
        if (caption is not null)
        {
            block.Children.Add(Overline(caption));
        }

        if (value is null)
        {
            block.Children.Add(Caption("КБЖУ пока неизвестны"));
            return block;
        }

        Label calories = new() { FontAutoScalingEnabled = true, FormattedText = new FormattedString() };
        calories.FormattedText.Spans.Add(new Span { Text = Kilocalories(value.Calories), FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = TextPrimary });
        calories.FormattedText.Spans.Add(new Span { Text = " ккал", FontSize = 15, TextColor = Muted });
        block.Children.Add(calories);
        block.Children.Add(MacroLine(value));
        return block;
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

    public static string Amount(decimal value) => value.ToString("0.##", Russian);

    public static string MacroAmount(decimal value) => value.ToString("0", Russian);

    public static string Kilocalories(decimal value) => value.ToString("#,0", Russian);

    public static string Nutrition(NutritionResponse value) =>
        $"{Amount(value.Calories)} ккал · Б {MacroAmount(value.ProteinGrams)} · Ж {MacroAmount(value.FatGrams)} · У {MacroAmount(value.CarbohydratesGrams)} г";

    public static string Nutrition(ProductResponse value) =>
        $"{Amount(value.Calories)} ккал · Б {MacroAmount(value.ProteinGrams)} · Ж {MacroAmount(value.FatGrams)} · У {MacroAmount(value.CarbohydratesGrams)} г";

    // screen readers spell out abbreviations like «Б» and «ккал», so spoken descriptions use whole words
    public static string SpokenNutrition(decimal calories, decimal protein, decimal fat, decimal carbs) =>
        $"{Amount(calories)} килокалорий, белки {MacroAmount(protein)}, жиры {MacroAmount(fat)}, углеводы {MacroAmount(carbs)} граммов";

    public static string SpokenNutrition(NutritionResponse value) =>
        SpokenNutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static string SpokenNutrition(ProductResponse value) =>
        SpokenNutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

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
