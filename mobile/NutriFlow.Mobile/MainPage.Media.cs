using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using Microsoft.Maui.Dispatching;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private Task LabelSourcePageAsync(Guid profileId)
    {
        FormPage page = new("Фото этикетки");
        page.Fields.Children.Add(Ui.Title("Пищевая ценность с упаковки"));
        page.Fields.Children.Add(Ui.Text("Снимите название и таблицу КБЖУ крупно. Фото отправится в Groq, затем вы сможете сверить и исправить каждое значение."));
        async Task ReadPhotoAsync(bool camera)
        {
            await page.RunAsync(async () =>
            {
                GroqSettings settings = await ProfileSettings.GetGroqAsync(profileId);
                FileResult? file;
                if (camera)
                {
                    PermissionStatus permission = await Permissions.RequestAsync<Permissions.Camera>();
                    if (permission != PermissionStatus.Granted)
                    {
                        throw new InvalidOperationException("Разрешите доступ к камере в настройках телефона или выберите фото из галереи.");
                    }

                    file = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Этикетка продукта" });
                }
                else
                {
                    file = (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Выбрать этикетку", SelectionLimit = 1 })).FirstOrDefault();
                }

                if (file is null)
                {
                    return;
                }

                byte[] bytes = await ReadFileAsync(file, 8 * 1024 * 1024);
                string mediaType = PhotoMediaType(file);
                LabelCapture capture = await _client.ReadLabelAsync(profileId, bytes, mediaType, settings);
                NutritionLabelDraft draft = capture.Draft;
                string basis = draft.Basis switch { NutritionBasis.Per100Grams => "на 100 г", NutritionBasis.Per100Milliliters => "на 100 мл", NutritionBasis.PerServing => "на порцию", _ => "не определена" };
                await Navigation.PopAsync();
                await ProductEditorPageAsync(profileId, draft.ProductName, photo: bytes, questions: draft.ClarificationQuestions, basis: basis, labelDraft: draft, photoReference: capture.PhotoReference);
            });
        }

        page.Fields.Children.Add(Ui.Action("Сделать фото", () => ReadPhotoAsync(true)));
        page.Fields.Children.Add(Ui.Action("Выбрать из галереи", () => ReadPhotoAsync(false), true));
        return Navigation.PushAsync(page);
    }

    private Task AudioPageAsync(Guid profileId)
    {
        FormPage page = new("Голос и аудио");
        AudioCapture capture = new();
        IDispatcherTimer timer = page.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        int elapsedSeconds = 0;
        bool transcribing = false;
        Label recordingStatus = Ui.Text("", true);
        Editor transcript = new() { Placeholder = "Здесь появится текст. Проверьте и исправьте его.", MinimumHeightRequest = 180, AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 4000, BackgroundColor = Colors.White };
        page.Fields.Children.Add(Ui.Title("Описание еды голосом"));
        page.Fields.Children.Add(Ui.Text("Распознавание отправляет аудио в Groq. Полученный текст можно исправить перед разбором еды.", true));
        page.Fields.Children.Add(recordingStatus);
        Button? start = null;
        Button? stop = null;
        Button? pick = null;
        async Task TranscribeAsync(byte[] bytes, string? mediaType)
        {
            transcribing = true;
            try
            {
                GroqSettings settings = await ProfileSettings.GetGroqAsync(profileId);
                transcript.Text = await _client.TranscribeAsync(profileId, bytes, mediaType, settings);
                recordingStatus.Text = "Текст готов — проверьте его перед использованием.";
            }
            finally
            {
                transcribing = false;
            }
        }

        async Task StopAsync()
        {
            timer.Stop();
            await page.RunAsync(async () =>
            {
                byte[] bytes = await capture.StopAsync();
                recordingStatus.Text = "Распознавание…";
                await TranscribeAsync(bytes, "audio/mp4");
            });
            start!.IsEnabled = true;
            stop!.IsEnabled = false;
            pick!.IsEnabled = true;
        }

        start = Ui.Action("Начать запись", () => page.RunAsync(async () =>
        {
            await ProfileSettings.GetGroqAsync(profileId);
            await capture.StartAsync();
            elapsedSeconds = 0;
            recordingStatus.Text = "Идёт запись. Нажмите «Остановить» после описания.";
            start!.IsEnabled = false;
            stop!.IsEnabled = true;
            pick!.IsEnabled = false;
            timer.Start();
        }));
        stop = Ui.Action("Остановить и распознать", StopAsync, true);
        stop.IsEnabled = false;
        pick = Ui.Action("Выбрать аудиофайл", () => page.RunAsync(async () =>
        {
            await ProfileSettings.GetGroqAsync(profileId);
            FileResult? file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выбрать аудио",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "audio/*" },
                    [DevicePlatform.iOS] = new[] { "public.audio" },
                    [DevicePlatform.WinUI] = new[] { ".m4a", ".mp3", ".wav", ".ogg", ".webm", ".mp4" },
                    [DevicePlatform.MacCatalyst] = new[] { "public.audio" }
                })
            });
            if (file is not null)
            {
                await TranscribeAsync(await ReadFileAsync(file, 8 * 1024 * 1024), AudioMediaType(file));
            }
        }), true);
#if ANDROID
        page.Fields.Children.Add(start);
        page.Fields.Children.Add(stop);
#endif
        page.Fields.Children.Add(pick);
        page.Fields.Children.Add(transcript);
        page.Fields.Children.Add(Ui.Action("Использовать проверенный текст", () => page.RunAsync(async () =>
        {
            if (capture.IsRecording || transcribing)
            {
                throw new InvalidOperationException("Сначала остановите запись и дождитесь распознавания.");
            }

            string text = (transcript.Text ?? "").Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("Введите или распознайте текст описания.");
            }

            _draftText = string.IsNullOrWhiteSpace(_draftText) ? text : $"{_draftText}\n{text}";
            _tab = "Ввод";
            await Navigation.PopAsync();
        })));
        timer.Tick += async (_, _) =>
        {
            elapsedSeconds++;
            recordingStatus.Text = $"Запись: {elapsedSeconds} с. Максимум 110 с.";
            if (elapsedSeconds >= 110)
            {
                await StopAsync();
            }
        };
        page.OnClose = () =>
        {
            timer.Stop();
            capture.Dispose();
        };
        return Navigation.PushAsync(page);
    }

    private static async Task<byte[]> ReadFileAsync(FileResult file, int maximumBytes)
    {
        await using Stream input = await file.OpenReadAsync();
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer)) != 0)
        {
            if (output.Length + count > maximumBytes)
            {
                throw new ArgumentException($"Файл слишком большой. Максимум {maximumBytes / (1024 * 1024)} МБ.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count));
        }

        return output.ToArray();
    }

    private static string PhotoMediaType(FileResult file) => Path.GetExtension(file.FileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => file.ContentType ?? "image/jpeg"
    };

    private static string? AudioMediaType(FileResult file) => Path.GetExtension(file.FileName).ToLowerInvariant() switch
    {
        ".m4a" or ".mp4" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".webm" => "audio/webm",
        _ => file.ContentType
    };
}
