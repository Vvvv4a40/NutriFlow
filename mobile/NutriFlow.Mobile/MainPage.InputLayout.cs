using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Domain;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View InputPurposeSelector(Action changed)
    {
        Button diary = new() { Text = "В дневник", AutomationId = "input-purpose-diary" };
        Button dish = new() { Text = "Создать блюдо", AutomationId = "input-purpose-dish" };

        void Refresh(Button button, MealSessionPurpose purpose)
        {
            bool selected = _purpose == purpose;
            button.FontSize = 13;
            button.Padding = new Thickness(10, 12);
            button.CornerRadius = 14;
            button.BackgroundColor = selected ? Ui.Accent : Colors.Transparent;
            button.TextColor = selected ? Ui.Background : Ui.Muted;
            button.BorderColor = selected ? Ui.Accent : Colors.Transparent;
            button.BorderWidth = 1;
            button.IsEnabled = _session is null;
            SemanticProperties.SetDescription(button, selected ? $"{button.Text}. Выбрано" : button.Text);
            if (_session is not null)
            {
                SemanticProperties.SetHint(button, "Режим черновика зафиксирован. Для изменения начните новую запись.");
            }
        }

        void Select(MealSessionPurpose purpose)
        {
            if (_busy || _session is not null || _purpose == purpose)
            {
                return;
            }

            _purpose = purpose;
            Refresh(diary, MealSessionPurpose.Diary);
            Refresh(dish, MealSessionPurpose.CreateDish);
            changed();
        }

        Refresh(diary, MealSessionPurpose.Diary);
        Refresh(dish, MealSessionPurpose.CreateDish);
        diary.Clicked += (_, _) => Select(MealSessionPurpose.Diary);
        dish.Clicked += (_, _) => Select(MealSessionPurpose.CreateDish);
        Grid choices = new()
        {
            ColumnSpacing = 6,
            ColumnDefinitions = new ColumnDefinitionCollection { new(), new() }
        };
        choices.Add(diary, 0);
        choices.Add(dish, 1);
        return new Border
        {
            Content = choices,
            BackgroundColor = Ui.Surface,
            Padding = 5,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 20 }
        };
    }

    private View InputComposer(Guid profileId, Editor editor, Label count, Button submit)
    {
        Label title = Ui.Text(_session is null ? "Описание еды" : "Дополнение");
        title.FontSize = 15;
        title.FontAttributes = FontAttributes.Bold;
        Grid heading = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        heading.Add(title, 0);
        heading.Add(count, 1);

        Button voice = Ui.Action("", () => AudioPageAsync(profileId), true);
        voice.AutomationId = "input-voice";
        voice.Padding = 0;
        voice.MinimumHeightRequest = 56;
        SemanticProperties.SetDescription(voice, "Голос или аудиофайл");
        SemanticProperties.SetHint(voice, "Распознать речь и проверить текст перед использованием");
        Label voiceText = Ui.Text("Голос");
        voiceText.FontSize = 13;
        voiceText.FontAttributes = FontAttributes.Bold;
        HorizontalStackLayout voiceContent = new() { Spacing = 6, InputTransparent = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        voiceContent.Children.Add(new AppIcon(AppIconKind.Microphone, Ui.Accent, 20));
        voiceContent.Children.Add(voiceText);
        voiceContent.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Grid voiceControl = new() { WidthRequest = 96 };
        voiceControl.Add(voice);
        voiceControl.Add(voiceContent);
        Grid actions = new()
        {
            ColumnSpacing = 8,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        actions.Add(voiceControl, 0);
        actions.Add(submit, 1);
        VerticalStackLayout composer = new() { Spacing = 12 };
        composer.Children.Add(heading);
        composer.Children.Add(editor);
        composer.Children.Add(actions);
        return new Border
        {
            Content = composer,
            Padding = 16,
            BackgroundColor = Ui.Surface,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 24 }
        };
    }

    private void RenderInputAlternatives(Guid profileId, VerticalStackLayout alternatives)
    {
        alternatives.Children.Clear();
        alternatives.IsVisible = _session is null;
        if (_session is not null)
        {
            return;
        }

        if (_purpose == MealSessionPurpose.CreateDish)
        {
            alternatives.Children.Add(InputShortcut("Из ингредиентов", AppIconKind.Dishes,
                "input-manual-dish", () => ManualDishPageAsync(profileId)));
        }
        else
        {
            alternatives.Children.Add(InputShortcut("Продукт", AppIconKind.Products,
                "input-manual-product", () => ChooseManualMealPageAsync(profileId)));
            alternatives.Children.Add(InputShortcut("Мои блюда", AppIconKind.Dishes,
                "input-saved-dishes", () => SelectTabAsync("Блюда")));
        }
    }

    private static View InputShortcut(string title, AppIconKind icon, string automationId, Func<Task> action)
    {
        Button button = Ui.Action("", action, true);
        button.AutomationId = automationId;
        button.BackgroundColor = Colors.Transparent;
        button.MinimumHeightRequest = 56;
        SemanticProperties.SetDescription(button, title);
        Label name = Ui.Text(title);
        name.FontAttributes = FontAttributes.Bold;
        name.VerticalOptions = LayoutOptions.Center;
        Grid content = new()
        {
            Padding = new Thickness(16, 14),
            ColumnSpacing = 12,
            InputTransparent = true,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        content.Add(new AppIcon(icon, Ui.Accent) { VerticalOptions = LayoutOptions.Center }, 0);
        content.Add(name, 1);
        content.Add(new AppIcon(AppIconKind.Next, Ui.Muted, 18) { VerticalOptions = LayoutOptions.Center }, 2);
        content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Grid row = new();
        row.Add(button);
        row.Add(content);
        return new Border { Content = row, BackgroundColor = Ui.Surface, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 20 } };
    }
}
