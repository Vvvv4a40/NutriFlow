namespace NutriFlow.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;
    private NavigationPage? _navigation;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
        ApplyThemeResources();
        // the page is created by DI before the application exists, so its shell starts in the fallback theme
        _mainPage.ApplyShellColors();
        RequestedThemeChanged += (_, _) => Dispatcher.Dispatch(() =>
        {
            ApplyThemeResources();
            ApplySystemBars();
            _mainPage.ApplyTheme();
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _navigation = new NavigationPage(_mainPage);
        ApplyThemeResources();
        Window window = new(_navigation);
        // a backgrounded app may be killed without warning, so pending typed text is written on the first pause
        window.Deactivated += (_, _) => _mainPage.SaveDelayedInputDraftNow();
        window.Activated += (_, _) => ApplySystemBars();
        return window;
    }

    // implicit styles in App.xaml read these keys, so a theme change restyles controls without rebuilding them
    private void ApplyThemeResources()
    {
        Resources["Background"] = Ui.Background;
        Resources["SurfaceRaised"] = Ui.SurfaceRaised;
        Resources["TextPrimary"] = Ui.TextPrimary;
        Resources["Muted"] = Ui.Muted;
        Resources["Subtle"] = Ui.Subtle;
        Resources["Accent"] = Ui.Accent;
        Resources["AccentPressed"] = Ui.AccentPressed;
        Resources["OnAccent"] = Ui.OnAccent;
        if (_navigation is not null)
        {
            _navigation.BarBackgroundColor = Ui.Background;
            _navigation.BarTextColor = Ui.TextPrimary;
        }
    }

    // edge-to-edge windows keep the status bar transparent, so its icons must follow the theme to stay visible
    private static void ApplySystemBars()
    {
#if ANDROID
        if (Platform.CurrentActivity?.Window is { } window)
        {
            if (AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView) is { } controller)
            {
                controller.AppearanceLightStatusBars = !Ui.IsDark;
                controller.AppearanceLightNavigationBars = !Ui.IsDark;
            }
        }
#endif
    }
}
