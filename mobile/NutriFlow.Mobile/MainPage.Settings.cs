namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderSettingsAsync(Guid profileId)
    {
        Label title = Ui.Title("Настройки");
        title.FontSize = 28;
        title.Margin = 0;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        _body.Children.Add(title);

        bool hasKey = await ProfileSettings.HasGroqAsync(profileId);
        Label provider = Ui.Title("Groq");
        provider.FontSize = 20;
        provider.Margin = 0;
        SemanticProperties.SetHeadingLevel(provider, SemanticHeadingLevel.Level2);
        Label status = Ui.Text(hasKey ? "Ключ сохранён" : "Ключ не добавлен", true);
        status.AutomationId = "settings-key-status";
        Entry apiKey = Ui.Field(hasKey ? "Новый ключ" : "Вставьте ключ");
        apiKey.AutomationId = "settings-api-key";
        apiKey.IsPassword = true;
        apiKey.IsSpellCheckEnabled = false;
        apiKey.IsTextPredictionEnabled = false;
        VerticalStackLayout connection = new() { Spacing = 12 };
        connection.Children.Add(provider);
        connection.Children.Add(status);
        connection.Children.Add(Ui.Text("Ключ только для этого профиля.", true));
        connection.Children.Add(Ui.Text("Текст, фото и аудио для AI отправляются в Groq.", true));
        connection.Children.Add(Ui.Text("Ключ Groq"));
        connection.Children.Add(apiKey);
        if (hasKey)
        {
            connection.Children.Add(Ui.Text("Пустое поле сохранит текущий ключ.", true));
        }

        VerticalStackLayout modelFields = new() { Spacing = 12, IsVisible = false, AutomationId = "settings-model-fields" };
        modelFields.Children.Add(Ui.Text("Текст"));
        Entry textModel = Ui.Field("Модель текста", ProfileSettings.TextModel(profileId));
        textModel.AutomationId = "settings-model-text";
        modelFields.Children.Add(textModel);
        modelFields.Children.Add(Ui.Text("Фото"));
        Entry visionModel = Ui.Field("Модель фото", ProfileSettings.VisionModel(profileId));
        visionModel.AutomationId = "settings-model-photo";
        modelFields.Children.Add(visionModel);
        modelFields.Children.Add(Ui.Text("Голос"));
        Entry speechModel = Ui.Field("Модель речи", ProfileSettings.SpeechModel(profileId));
        speechModel.AutomationId = "settings-model-speech";
        modelFields.Children.Add(speechModel);
        foreach (Entry model in new[] { textModel, visionModel, speechModel })
        {
            model.IsSpellCheckEnabled = false;
            model.IsTextPredictionEnabled = false;
        }

        Button models = Ui.Action("Модели AI", () =>
        {
            modelFields.IsVisible = !modelFields.IsVisible;
            return Task.CompletedTask;
        }, true);
        models.AutomationId = "settings-model-toggle";
        SemanticProperties.SetHint(models, "Показать или скрыть модели текста, фото и голоса");
        connection.Children.Add(models);
        connection.Children.Add(modelFields);
        Button save = Ui.Action("Сохранить", () => RunAsync(async () =>
        {
            if (!hasKey && string.IsNullOrWhiteSpace(apiKey.Text))
            {
                throw new ArgumentException("Введите личный ключ Groq.");
            }

            await ProfileSettings.SaveGroqAsync(profileId, apiKey.Text, Ui.Name(textModel, "Модель текста"), Ui.Name(visionModel, "Модель фото"), Ui.Name(speechModel, "Модель речи"));
            apiKey.Text = "";
            await RenderAsync();
            await DisplayAlertAsync("Сохранено", "Настройки применены к этому профилю.", "Готово");
        }));
        save.AutomationId = "settings-save";
        connection.Children.Add(save);
        if (hasKey)
        {
            Button remove = Ui.Action("Удалить ключ", () => RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Удалить ключ?", "AI-функции этого профиля перестанут работать до добавления ключа.", "Удалить", "Отмена"))
                {
                    ProfileSettings.RemoveGroq(profileId);
                    await RenderAsync();
                }
            }), true);
            remove.AutomationId = "settings-key-remove";
            connection.Children.Add(remove);
        }

        Button console = Ui.Action("Получить ключ", () => RunAsync(() => Launcher.Default.OpenAsync("https://console.groq.com/keys")), true);
        console.AutomationId = "settings-key-console";
        connection.Children.Add(console);
        _body.Children.Add(Ui.Card(connection));

        Label storage = Ui.Title("На устройстве");
        storage.FontSize = 20;
        storage.Margin = 0;
        SemanticProperties.SetHeadingLevel(storage, SemanticHeadingLevel.Level2);
        Label storageNote = Ui.Text("Без синхронизации. При удалении приложения дневник, продукты и блюда будут потеряны без сохранённой копии.", true);
        storageNote.AutomationId = "settings-storage-note";
        Button export = Ui.Action("Сохранить копию", () => ExportBackupAsync(profileId), true);
        export.AutomationId = "settings-backup-export";
        Button restore = Ui.Action("Восстановить копию", RestoreBackupPageAsync, true);
        restore.AutomationId = "settings-backup-restore";
        _body.Children.Add(Ui.Card(storage, storageNote,
            Ui.Text("Копия содержит личные данные и фото, но не ключ Groq. Архив не зашифрован.", true),
            export, restore));
    }
}
