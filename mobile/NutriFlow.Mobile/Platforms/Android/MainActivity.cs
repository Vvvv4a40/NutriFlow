using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;

namespace NutriFlow.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ActivityResultLauncher? _backupDocumentLauncher;
    private BackupDocumentCallback? _backupDocumentCallback;
    private TaskCompletionSource<Android.Net.Uri?>? _backupDocumentCompletion;
    private bool _destroyed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _backupDocumentCallback = new BackupDocumentCallback(this);
        _backupDocumentLauncher = RegisterForActivityResult(
            new ActivityResultContracts.CreateDocument("application/zip"), _backupDocumentCallback);
    }

    internal Task<Android.Net.Uri?> CreateBackupDocumentAsync(string fileName)
    {
        if (_destroyed || _backupDocumentLauncher is null)
        {
            throw new InvalidOperationException("Откройте приложение и повторите сохранение.");
        }
        if (_backupDocumentCompletion is not null)
        {
            throw new InvalidOperationException("Сначала завершите предыдущее сохранение.");
        }

        TaskCompletionSource<Android.Net.Uri?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _backupDocumentCompletion = completion;
        try
        {
            using Java.Lang.String suggestedName = new(fileName);
            _backupDocumentLauncher.Launch(suggestedName);
        }
        catch
        {
            _backupDocumentCompletion = null;
            throw;
        }
        return completion.Task;
    }

    protected override void OnDestroy()
    {
        _destroyed = true;
        TaskCompletionSource<Android.Net.Uri?>? completion = _backupDocumentCompletion;
        _backupDocumentCompletion = null;
        completion?.TrySetCanceled();
        base.OnDestroy();
    }

    private void CompleteBackupDocument(Java.Lang.Object? result)
    {
        TaskCompletionSource<Android.Net.Uri?>? completion = _backupDocumentCompletion;
        _backupDocumentCompletion = null;
        if (result is null || result is Android.Net.Uri)
        {
            completion?.TrySetResult(result as Android.Net.Uri);
        }
        else
        {
            completion?.TrySetException(new IOException("Система вернула неподдерживаемый файл."));
        }
    }

    private sealed class BackupDocumentCallback(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result) => activity.CompleteBackupDocument(result);
    }
}
