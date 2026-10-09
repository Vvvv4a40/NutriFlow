namespace NutriFlow.Mobile;

internal sealed class AudioCapture : IDisposable
{
#if ANDROID
    private Android.Media.MediaRecorder? _recorder;
    private string? _path;
#endif

    public bool IsRecording { get; private set; }

    public async Task StartAsync()
    {
#if ANDROID
        PermissionStatus permission = await Permissions.RequestAsync<Permissions.Microphone>();
        if (permission != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Для записи разрешите доступ к микрофону в настройках телефона.");
        }

        _path = Path.Combine(FileSystem.CacheDirectory, $"voice-{Guid.NewGuid():N}.m4a");
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            _recorder = new Android.Media.MediaRecorder(Android.App.Application.Context);
        }
        else
        {
#pragma warning disable CS0618
            _recorder = new Android.Media.MediaRecorder();
#pragma warning restore CS0618
        }

        try
        {
            _recorder.SetAudioSource(Android.Media.AudioSource.Mic);
            _recorder.SetOutputFormat(Android.Media.OutputFormat.Mpeg4);
            _recorder.SetAudioEncoder(Android.Media.AudioEncoder.Aac);
            _recorder.SetAudioSamplingRate(44100);
            _recorder.SetAudioEncodingBitRate(96000);
            _recorder.SetMaxDuration(120000);
            _recorder.SetOutputFile(_path);
            _recorder.Prepare();
            _recorder.Start();
            IsRecording = true;
        }
        catch
        {
            Dispose();
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("На этом устройстве выберите готовый аудиофайл.");
#endif
    }

    public async Task<byte[]> StopAsync()
    {
#if ANDROID
        if (_recorder is null || _path is null || !IsRecording)
        {
            throw new InvalidOperationException("Запись ещё не началась.");
        }

        string path = _path;
        try
        {
            _recorder.Stop();
            _recorder.Release();
            _recorder.Dispose();
            _recorder = null;
            IsRecording = false;
            return await File.ReadAllBytesAsync(path);
        }
        catch (Exception exception) when (exception is Java.Lang.RuntimeException or IOException)
        {
            throw new InvalidOperationException("Запись слишком короткая, достигла лимита или микрофон занят. Попробуйте ещё раз.", exception);
        }
        finally
        {
            Dispose();
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("На этом устройстве выберите готовый аудиофайл.");
#endif
    }

    public void Dispose()
    {
#if ANDROID
        _recorder?.Release();
        _recorder?.Dispose();
        _recorder = null;
        if (_path is not null && File.Exists(_path))
        {
            File.Delete(_path);
        }

        _path = null;
#endif
        IsRecording = false;
    }
}
