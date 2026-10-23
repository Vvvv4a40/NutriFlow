using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Domain;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View InputPurposeSelector(Action changed)
    {
        ContentView container = new();
        (string, string)[] options = [("В дневник", "input-purpose-diary"), ("Создать блюдо", "input-purpose-dish")];

        void Render()
        {
            container.Content = Ui.Segmented(options, _purpose == MealSessionPurpose.CreateDish ? 1 : 0, Select,
                _session is null && !InputDraftBlocked);
        }

        void Select(int index)
        {
            MealSessionPurpose purpose = index == 1 ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary;
            if (Busy || _session is not null || _purpose == purpose || InputDraftBlocked)
            {
                return;
            }

            _purpose = purpose;
            _ = QueueInputDraftSave(CurrentProfileId(), CaptureInputDraft());
            Render();
            changed();
        }

        Render();
        return container;
    }

    private View InputComposer(Guid profileId, Editor editor, Label count, Button submit, string? title)
    {
        VerticalStackLayout composer = new() { Spacing = 12 };
        if (title is not null)
        {
            composer.Children.Add(Ui.Overline(title));
        }

        composer.Children.Add(editor);
        Grid tools = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        HorizontalStackLayout voiceContent = new() { Spacing = 8, Padding = new Thickness(14, 0, 16, 0), VerticalOptions = LayoutOptions.Center };
        voiceContent.Children.Add(new AppIcon(AppIconKind.Microphone, Ui.Accent, 20) { VerticalOptions = LayoutOptions.Center });
        Label voiceText = Ui.Strong("Голос", 15);
        voiceText.TextColor = Ui.Accent;
        voiceText.VerticalOptions = LayoutOptions.Center;
        voiceContent.Children.Add(voiceText);
        Border voiceChip = new()
        {
            Content = voiceContent,
            HeightRequest = 44,
            BackgroundColor = Ui.AccentSoft,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 22 }
        };
        Grid voice = Ui.Pressable(voiceChip, () => AudioPageAsync(profileId), "Голос или аудиофайл",
            "Распознать речь и проверить текст перед использованием", "input-voice");
        Ui.FillPressable(voice, voiceChip, Ui.AccentSoft);
        voice.IsEnabled = !InputDraftBlocked;
        voice.VerticalOptions = LayoutOptions.Center;
        tools.Add(voice, 0);
        if (_inputSaveStatus is not null)
        {
            _inputSaveStatus.VerticalOptions = LayoutOptions.Center;
            tools.Add(_inputSaveStatus, 1);
        }

        count.VerticalOptions = LayoutOptions.Center;
        tools.Add(count, 2);
        composer.Children.Add(tools);
        composer.Children.Add(submit);
        return Ui.Panel(composer, 16);
    }

    private void RenderInputAlternatives(Guid profileId, VerticalStackLayout alternatives)
    {
        alternatives.Children.Clear();
        alternatives.IsVisible = _session is null && !InputDraftBlocked;
        if (_session is not null || InputDraftBlocked)
        {
            return;
        }

        alternatives.Children.Add(SectionHeader("Или без описания"));
        if (_purpose == MealSessionPurpose.CreateDish)
        {
            alternatives.Children.Add(Ui.List([
                Ui.Row("Собрать из продуктов", "Ингредиенты из каталога и готовый вес", null, null,
                    () => ManualDishPageAsync(profileId), "Собрать блюдо из продуктов каталога", AppIconKind.Dishes,
                    automationId: "input-manual-dish")
            ]));
        }
        else
        {
            alternatives.Children.Add(Ui.List([
                Ui.Row("Продукт из каталога", "Указать вес порции", null, null,
                    () => ChooseManualMealPageAsync(profileId), "Продукт из каталога", AppIconKind.Products,
                    automationId: "input-manual-product"),
                Ui.Row("Моё блюдо", "Порция сохранённого блюда", null, null,
                    () => SelectTabAsync("Блюда"), "Мои блюда", AppIconKind.Dishes,
                    automationId: "input-saved-dishes")
            ]));
        }
    }
}
