namespace NutriFlow.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;
    private NavigationPage? _navigation;
    private static bool _darkNavigationBar;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
        ApplyThemeResources();
        Ui.ThemeChanged += ApplyThemeResources;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _navigation = new NavigationPage(_mainPage);
        ApplyThemeResources();
        Window window = new(_navigation);
        // a backgrounded app may be killed without warning, so pending typed text is written on the first pause
        window.Deactivated += (_, _) => _mainPage.SaveDelayedInputDraftNow();
        window.Activated += (_, _) => SetSystemBars(_darkNavigationBar);
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
        Resources["AccentFill"] = Ui.AccentFill;
        Resources["AccentPressed"] = Ui.AccentPressed;
        Resources["OnAccent"] = Ui.OnAccent;
        if (_navigation is not null)
        {
            _navigation.BarBackgroundColor = Ui.Background;
            _navigation.BarTextColor = Ui.TextPrimary;
        }
    }

    // edge-to-edge windows keep the system bars transparent, so their icons follow whatever is drawn under them:
    // the page background at the top and, at the bottom, the navigation panel or the page
    internal static void SetSystemBars(bool darkNavigationBar)
    {
        _darkNavigationBar = darkNavigationBar;
#if ANDROID
        if (Platform.CurrentActivity?.Window is { } window
            && AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView) is { } controller)
        {
            controller.AppearanceLightStatusBars = !Ui.IsDark;
            controller.AppearanceLightNavigationBars = !darkNavigationBar;
        }
#endif
    }
}
