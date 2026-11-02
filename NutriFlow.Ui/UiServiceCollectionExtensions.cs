using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Ui.Dialogs;
using NutriFlow.Ui.Navigation;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui;

public static class UiServiceCollectionExtensions
{
    // one runner, one dialog queue and one back stack for the whole app, as in the single window of the phone app
    public static IServiceCollection AddNutriFlowUi(this IServiceCollection services)
    {
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<BackStack>();
        return services;
    }
}
