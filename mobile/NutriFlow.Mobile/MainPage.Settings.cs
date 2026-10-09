namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderSettingsAsync(Guid profileId)
    {
        _body.Children.Add(Ui.Title("Настройки Groq"));
        _body.Children.Add(Ui.Text("Ключ привязан к выбранному локальному профилю и хранится в защищённом хранилище телефона."));
        _body.Children.Add(Ui.Text("При использовании AI описание еды, фото этикетки или аудио отправляется напрямую в Groq. Ручной ввод, расчёты и дневник работают локально.", true));
        bool hasKey = await ProfileSettings.HasGroqAsync(profileId);
        _body.Children.Add(Ui.Text(hasKey ? "Личный ключ сохранён" : "Личный ключ пока не добавлен"));
        Entry apiKey = Ui.Field(hasKey ? "Новый ключ (пусто — сохранить текущий)" : "Личный ключ Groq");
        apiKey.IsPassword = true;
        apiKey.IsSpellCheckEnabled = false;
        apiKey.IsTextPredictionEnabled = false;
        _body.Children.Add(apiKey);
        _body.Children.Add(Ui.Text("Модель для текста"));
        Entry textModel = Ui.Field("Модель текста", ProfileSettings.TextModel(profileId));
        _body.Children.Add(textModel);
        _body.Children.Add(Ui.Text("Модель для фото (с поддержкой изображений)"));
        Entry visionModel = Ui.Field("Модель фото", ProfileSettings.VisionModel(profileId));
        _body.Children.Add(visionModel);
        _body.Children.Add(Ui.Text("Модель распознавания речи"));
        Entry speechModel = Ui.Field("Модель речи", ProfileSettings.SpeechModel(profileId));
        _body.Children.Add(speechModel);
        _body.Children.Add(Ui.Action("Сохранить настройки", () => RunAsync(async () =>
        {
            if (!hasKey && string.IsNullOrWhiteSpace(apiKey.Text))
            {
                throw new ArgumentException("Введите личный ключ Groq.");
            }

            await ProfileSettings.SaveGroqAsync(profileId, apiKey.Text, Ui.Name(textModel, "Модель текста"), Ui.Name(visionModel, "Модель фото"), Ui.Name(speechModel, "Модель речи"));
            apiKey.Text = "";
            await RenderAsync();
            await DisplayAlertAsync("Сохранено", "Настройки применены к этому профилю.", "Готово");
        })));
        if (hasKey)
        {
            _body.Children.Add(Ui.Action("Удалить ключ с телефона", () => RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Удалить ключ?", "AI-функции этого профиля перестанут работать до добавления ключа.", "Удалить", "Отмена"))
                {
                    ProfileSettings.RemoveGroq(profileId);
                    await RenderAsync();
                }
            }), true));
        }

        _body.Children.Add(Ui.Action("Получить ключ в Groq Console", () => RunAsync(() => Launcher.Default.OpenAsync("https://console.groq.com/keys")), true));
        _body.Children.Add(Ui.Card(Ui.Text("Хранение данных"), Ui.Text("Профили и записи находятся в памяти приложения. Удаление приложения удалит локальные данные. Ключи не включаются в APK.", true)));
    }
}
