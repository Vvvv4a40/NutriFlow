using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Pages;

internal static class AppInstall
{
    // the installed app opens in its own window while this one still holds the profile, and only one may run
    public static Task RunAsync(ActionRunner runner, IAppHost host) => runner.RunAsync(async () =>
    {
        if (await host.InstallAsync())
        {
            await runner.AlertAsync("NutriFlow установлен",
                "Откройте его с иконки, а это окно закройте: одновременно может работать только одно окно NutriFlow.", "Понятно");
        }
    });
}
