using NutriFlow.Mobile.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NutriFlow.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton(_ => new LocalProfileStore(Path.Combine(FileSystem.AppDataDirectory, "nutriflow")));
        builder.Services.AddSingleton<LocalMealClient>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
