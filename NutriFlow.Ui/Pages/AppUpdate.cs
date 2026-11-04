using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Pages;

internal static class AppUpdate
{
    public const string ProgressKey = "app-update";

    // the typed text is on the device before the restart; if it cannot be saved the old version keeps running
    public static Task RunAsync(ActionRunner runner, CaptureState capture, IAppHost host) => runner.RunAsync(async () =>
    {
        runner.ShowProgress(ProgressKey, "Обновляю…");
        await capture.FlushAsync();
        await host.SaveProfileFilesAsync();
        await host.ApplyUpdateAsync();
    });
}
