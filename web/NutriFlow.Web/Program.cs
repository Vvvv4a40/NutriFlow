using System.Globalization;
using Bit.Bsqlite.Storage.Opfs;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui;
using NutriFlow.Ui.Hosting;
using NutriFlow.Web;
using NutriFlow.Web.Diagnostics;
using NutriFlow.Web.Services;

// boot.mjs starts blazor with the same culture so that the russian icu data is loaded
CultureInfo culture = new("ru-RU");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddSingleton(new AppInfo(typeof(App).Assembly.GetName().Version!, builder.Configuration["Channel"]));

// only opfs: a fallback to a volatile backend would lose the diary at the next reload
builder.Services.AddBitBsqliteServices(sqlite => sqlite
    .AddStorageProvider<BsqliteOpfsStorageProvider>()
    .UseStorage(storage => storage.Prefer(BsqliteOpfsStorageProvider.ProviderName))
    .UseBindingStore<BsqliteBindingStore>());
builder.Services.AddScoped<WebProfileDatabase>();
builder.Services.AddSingleton<OpfsFiles>();
builder.Services.AddSingleton(services => new PersistentDirectory(services.GetRequiredService<OpfsFiles>()));
builder.Services.AddSingleton(_ => new LocalProfileStore(PersistentDirectory.MemoryRoot));
builder.Services.AddSingleton(services => new LocalInputDraftStore(services.GetRequiredService<LocalProfileStore>()));
// in development the Groq key «fake» parses text without network (Diagnostics/FakeGroqHandler.cs)
Func<HttpClient>? httpClients = builder.HostEnvironment.IsDevelopment()
    ? () => new HttpClient(new FakeGroqHandler(new HttpClientHandler()))
    : null;
builder.Services.AddScoped(services => new LocalMealClient(
    services.GetRequiredService<LocalProfileStore>(),
    httpClients,
    services.GetRequiredService<WebProfileDatabase>().OpenAsync));
builder.Services.AddSingleton<WebProfileSettings>();
builder.Services.AddSingleton<IProfileSettings>(services => services.GetRequiredService<WebProfileSettings>());
builder.Services.AddSingleton<BrowserPlatform>();
builder.Services.AddSingleton<IAppHost, WebAppHost>();
builder.Services.AddNutriFlowUi();

WebAssemblyHost host = builder.Build();
// the profile folder has to be back in memory before LocalProfileStore reads it
await host.Services.GetRequiredService<PersistentDirectory>().RestoreAsync();
await host.Services.GetRequiredService<BrowserPlatform>().StartAsync();
await host.RunAsync();
