using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using Microsoft.Maui.Dispatching;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private Task LabelSourcePageAsync(Guid profileId)
    {
        FormPage page = new("Фото этикетки");
        Border tile = Ui.IconTile(AppIconKind.Camera, Ui.Accent, 64);
        tile.HorizontalOptions = LayoutOptions.Start;
        page.Fields.Children.Add(tile);
        page.Fields.Children.Add(Ui.Headline("Пищевая ценность с упаковки", 24));
        page.Fields.Children.Add(Ui.Text("Фото отправится в Groq. Затем вы сверите и при необходимости исправите каждое значение.", true));
        VerticalStackLayout tips = new() { Spacing = 10 };
        tips.Children.Add(Tip("Таблица КБЖУ крупно и без бликов"));
        tips.Children.Add(Tip("Название продукта тоже в кадре"));
        tips.Children.Add(Tip("Видно, на 100 г или на порцию указаны значения"));
        page.Fields.Children.Add(Ui.Card(tips));
        Button? takePhoto = null;
        Button? pickPhoto = null;
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
                page.ShowProgress(camera ? takePhoto! : pickPhoto!, "Распознаю этикетку…");
                LabelCapture capture = await _client.ReadLabelAsync(profileId, bytes, mediaType, settings);
                NutritionLabelDraft draft = capture.Draft;
                string basis = draft.Basis switch { NutritionBasis.Per100Grams => "на 100 г", NutritionBasis.Per100Milliliters => "на 100 мл", NutritionBasis.PerServing => "на порцию", _ => "не определена" };
                await Navigation.PopAsync();
                await ProductEditorPageAsync(profileId, draft.ProductName, photo: bytes, questions: draft.ClarificationQuestions, basis: basis, labelDraft: draft, photoReference: capture.PhotoReference);
            });
        }

        takePhoto = Ui.Action("Сделать фото", () => ReadPhotoAsync(true));
        pickPhoto = Ui.Action("Выбрать из галереи", () => ReadPhotoAsync(false), true);
        page.AddFooter(takePhoto);
        page.AddFooter(pickPhoto);
        return Navigation.PushAsync(page);
    }

    private static View Tip(string text)
    {
        Grid row = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        row.Add(new AppIcon(AppIconKind.Check, Ui.Success, 18) { VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) }, 0);
        row.Add(Ui.Text(text), 1);
        return row;
    }

    private Task AudioPageAsync(Guid profileId)
    {
        FormPage page = new("Голосом");
        AudioCapture capture = new();
        IDispatcherTimer timer = page.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        int elapsedSeconds = 0;
        bool transcribing = false;
        Label recordingStatus = Ui.Text("Расскажите, что и сколько съели. Запись до 110 секунд.", true);
        recordingStatus.HorizontalTextAlignment = TextAlignment.Center;
        Editor transcript = new InputEditor() { Placeholder = "Здесь появится распознанный текст — его можно исправить", MinimumHeightRequest = 160, AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = MealInputText.MaximumLength, BackgroundColor = Ui.SurfaceRaised, PlaceholderColor = Ui.Subtle, FontSize = 17 };
        Button? start = null;
        Button? stop = null;
        Button? pick = null;
        Button? retry = null;
        (byte[] Bytes, string? MediaType)? lastAudio = null;
        async Task TranscribeAsync(byte[] bytes, string? mediaType, Button progress)
        {
            transcribing = true;
            lastAudio = (bytes, mediaType);
            retry!.IsVisible = false;
            try
            {
                GroqSettings settings = await ProfileSettings.GetGroqAsync(profileId);
                page.ShowProgress(progress, "Распознаю…");
                transcript.Text = await _client.TranscribeAsync(profileId, bytes, mediaType, settings);
                recordingStatus.Text = "Текст готов — проверьте его перед использованием.";
            }
            catch
            {
                // the recording is already stopped, so keep it and let the user retry instead of recording again
                recordingStatus.Text = "Не удалось распознать запись. Её можно отправить ещё раз.";
                retry.IsVisible = true;
                throw;
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
                await TranscribeAsync(bytes, "audio/mp4", stop!);
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
        stop = Ui.Action("Остановить", StopAsync, true);
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
                await TranscribeAsync(await ReadFileAsync(file, 8 * 1024 * 1024), AudioMediaType(file), pick!);
            }
        }), true);
        retry = Ui.Ghost("Повторить распознавание", () => page.RunAsync(() =>
            lastAudio is { } audio ? TranscribeAsync(audio.Bytes, audio.MediaType, retry!) : Task.CompletedTask), Ui.Warning);
        retry.IsVisible = false;
        retry.HorizontalOptions = LayoutOptions.Center;
        start.MinimumHeightRequest = 56;
        stop.MinimumHeightRequest = 56;
        pick.HorizontalOptions = LayoutOptions.Center;
        pick.BackgroundColor = Colors.Transparent;
        pick.TextColor = Ui.Accent;
        Border tile = Ui.IconTile(AppIconKind.Microphone, Ui.Accent, 88);
        tile.HorizontalOptions = LayoutOptions.Center;
        tile.Margin = new Thickness(0, 12, 0, 0);
        page.Fields.Children.Add(tile);
        page.Fields.Children.Add(recordingStatus);
        page.Fields.Children.Add(retry);
#if ANDROID
        Grid controls = new() { ColumnSpacing = 10, ColumnDefinitions = new ColumnDefinitionCollection { new(), new() } };
        controls.Add(start, 0);
        controls.Add(stop, 1);
        page.Fields.Children.Add(controls);
#endif
        page.Fields.Children.Add(pick);
        page.Fields.Children.Add(Ui.Labeled("Распознанный текст", transcript));
        page.Fields.Children.Add(Ui.Caption("Аудио отправляется в Groq только для распознавания. Текст попадёт в описание еды после вашей проверки.", Ui.Subtle));
        Button use = Ui.Action("Использовать текст", () => page.RunAsync(async () =>
        {
            if (capture.IsRecording || transcribing)
            {
                throw new InvalidOperationException("Сначала остановите запись и дождитесь распознавания.");
            }

            if (InputDraftBlocked || _profile?.Id != profileId)
            {
                throw new InvalidOperationException("Сначала восстановите или удалите сохранённый ввод.");
            }

            string combinedText = MealInputText.AppendTranscript(_draftText, transcript.Text);
            await QueueInputDraftSave(profileId, CaptureInputDraft(combinedText));
            await FlushInputDraftAsync();
            _draftText = combinedText;
            _tab = "Ввод";
            await Navigation.PopAsync();
        }));
        // the main action stays inactive until there is recognised or typed text to use
        Ui.SetEnabled(use, false);
        transcript.TextChanged += (_, _) => Ui.SetEnabled(use, !string.IsNullOrWhiteSpace(transcript.Text));
        page.AddFooter(use);
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
