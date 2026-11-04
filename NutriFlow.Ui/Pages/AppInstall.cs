using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Pages;

internal static class AppInstall
{
    // the installed app shares the data of this browser; when it opens, this tab saves its input and gives way
    public static Task RunAsync(ActionRunner runner, IAppHost host) => runner.RunAsync(async () =>
    {
        if (await host.InstallAsync())
        {
            await runner.AlertAsync("NutriFlow установлен",
                "Откройте его с иконки: там те же записи, а эту вкладку можно закрыть.", "Понятно");
        }
    });
}
