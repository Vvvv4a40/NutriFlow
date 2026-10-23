namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderSettingsAsync(Guid profileId)
    {
        _body.Children.Add(SectionHeader("Профиль"));
        _body.Children.Add(Ui.List([
            Ui.Row(_profile?.Name ?? "Профиль", "Профили не видят данные друг друга", null, null, OpenProfilesAsync,
                $"Профиль {_profile?.Name}. Сменить или создать профиль", AppIconKind.User, automationId: "settings-profile")
        ]));

        bool hasKey = await ProfileSettings.HasGroqAsync(profileId);
        _body.Children.Add(SectionHeader("Распознавание"));
        Grid provider = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        provider.Add(Ui.IconTile(AppIconKind.Sparkle, Ui.Accent), 0);
        VerticalStackLayout providerText = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        providerText.Children.Add(Ui.Strong("Groq"));
        providerText.Children.Add(Ui.Caption("Текст, фото этикеток и голос"));
        provider.Add(providerText, 1);
        Border status = Ui.Badge(hasKey ? "Подключено" : "Нет ключа", hasKey ? Ui.Success : Ui.Warning);
        Label statusText = (Label)status.Content!;
        statusText.AutomationId = "settings-key-status";
        provider.Add(status, 2);

        Entry apiKey = Ui.Field(hasKey ? "Вставьте новый ключ, чтобы заменить" : "gsk_…");
        apiKey.AutomationId = "settings-api-key";
        apiKey.IsPassword = true;
        apiKey.IsSpellCheckEnabled = false;
        apiKey.IsTextPredictionEnabled = false;

        VerticalStackLayout modelFields = new() { Spacing = 14, AutomationId = "settings-model-fields" };
        Entry textModel = Ui.Field("Модель текста", ProfileSettings.TextModel(profileId));
        textModel.AutomationId = "settings-model-text";
        modelFields.Children.Add(Ui.Labeled("Текст", textModel));
        Entry visionModel = Ui.Field("Модель фото", ProfileSettings.VisionModel(profileId));
        visionModel.AutomationId = "settings-model-photo";
        modelFields.Children.Add(Ui.Labeled("Фото", visionModel));
        Entry speechModel = Ui.Field("Модель речи", ProfileSettings.SpeechModel(profileId));
        speechModel.AutomationId = "settings-model-speech";
        modelFields.Children.Add(Ui.Labeled("Голос", speechModel));
        foreach (Entry model in new[] { textModel, visionModel, speechModel })
        {
            model.IsSpellCheckEnabled = false;
            model.IsTextPredictionEnabled = false;
        }

        Button save = Ui.Action(hasKey ? "Сохранить изменения" : "Подключить", () => RunAsync(async () =>
        {
            if (!hasKey && string.IsNullOrWhiteSpace(apiKey.Text))
            {
                throw new ArgumentException("Вставьте личный ключ Groq.");
            }

            await ProfileSettings.SaveGroqAsync(profileId, apiKey.Text, Ui.Name(textModel, "Модель текста"), Ui.Name(visionModel, "Модель фото"), Ui.Name(speechModel, "Модель речи"));
            apiKey.Text = "";
            await RenderAsync();
            await DisplayAlertAsync("Сохранено", "Настройки применены к этому профилю.", "Готово");
        }), hasKey);
        save.AutomationId = "settings-save";

        Button console = Ui.Ghost("Получить ключ на console.groq.com", () => RunAsync(() => Launcher.Default.OpenAsync("https://console.groq.com/keys")));
        console.AutomationId = "settings-key-console";
        console.HorizontalOptions = LayoutOptions.Start;
        console.Margin = new Thickness(-12, -6, 0, -6);

        VerticalStackLayout connection = new() { Spacing = 14 };
        connection.Children.Add(provider);
        connection.Children.Add(Ui.Divider());
        connection.Children.Add(Ui.Labeled(hasKey ? "Новый ключ Groq" : "Ключ Groq", apiKey));
        connection.Children.Add(Ui.Caption(hasKey
            ? "Пустое поле оставит текущий ключ. Ключ хранится только в этом профиле."
            : "Ключ личный и хранится только в этом профиле. Текст, фото и аудио для распознавания отправляются в Groq."));
        connection.Children.Add(console);
        connection.Children.Add(Ui.Expander("Модели", modelFields, "settings-model-toggle"));
        connection.Children.Add(save);
        if (hasKey)
        {
            Button remove = Ui.Ghost("Удалить ключ", () => RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Удалить ключ?", "Распознавание текста, фото и голоса в этом профиле перестанет работать, пока не добавите ключ снова.", "Удалить", "Оставить"))
                {
                    ProfileSettings.RemoveGroq(profileId);
                    await RenderAsync();
                }
            }), Ui.Danger);
            remove.AutomationId = "settings-key-remove";
            remove.HorizontalOptions = LayoutOptions.Center;
            connection.Children.Add(remove);
        }

        _body.Children.Add(Ui.Card(connection));

        _body.Children.Add(SectionHeader("Данные на устройстве"));
        Label storageNote = Ui.Caption("Синхронизации нет. Если удалить приложение без копии, дневник, продукты и блюда пропадут. Копия содержит личные данные и фото без ключа Groq и не зашифрована.");
        storageNote.AutomationId = "settings-storage-note";
        storageNote.Margin = new Thickness(4, 0);
        _body.Children.Add(Ui.List([
            Ui.Row("Сохранить копию", "ZIP-архив текущего профиля", null, null, () => ExportBackupAsync(profileId),
                "Сохранить копию профиля", AppIconKind.Archive, automationId: "settings-backup-export"),
            Ui.Row("Восстановить из копии", "В новый профиль, текущие данные не изменятся", null, null, () => RunAsync(RestoreBackupPageAsync),
                "Восстановить из копии", AppIconKind.Refresh, automationId: "settings-backup-restore")
        ]));
        _body.Children.Add(storageNote);

        Label version = Ui.Caption($"NutriFlow {AppInfo.Current.VersionString}", Ui.Subtle);
        version.FontSize = 12;
        version.HorizontalTextAlignment = TextAlignment.Center;
        version.Margin = new Thickness(0, 12, 0, 0);
        _body.Children.Add(version);
    }
}
