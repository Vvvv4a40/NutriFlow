using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Dialogs;
using NutriFlow.Ui.Navigation;
using NutriFlow.Ui.Profiles;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui;

public static class UiServiceCollectionExtensions
{
    // one runner, one dialog queue and one back stack for the whole app, as in the single window of the phone app.
    // the host registers LocalProfileStore, LocalInputDraftStore, LocalMealClient, LocalProfileBackup, IProfileSettings and IAppHost
    public static IServiceCollection AddNutriFlowUi(this IServiceCollection services)
    {
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<BackStack>();
        services.AddSingleton<ProfileState>();
        // scoped like LocalMealClient, which it uses; the browser has one scope for the whole app
        services.AddScoped<CaptureState>();
        services.AddScoped<ProfileBackups>();
        return services;
    }
}
