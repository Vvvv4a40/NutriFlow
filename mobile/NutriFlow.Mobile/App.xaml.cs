namespace NutriFlow.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window = new(new NavigationPage(_mainPage)
        {
            BarBackgroundColor = Ui.Background,
            BarTextColor = Ui.TextPrimary
        });
        // a backgrounded app may be killed without warning, so pending typed text is written on the first pause
        window.Deactivated += (_, _) => _mainPage.SaveDelayedInputDraftNow();
        return window;
    }
}
