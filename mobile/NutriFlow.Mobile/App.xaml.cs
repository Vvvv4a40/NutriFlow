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
        return new Window(new NavigationPage(_mainPage)
        {
            BarBackgroundColor = Ui.Background,
            BarTextColor = Ui.TextPrimary
        });
    }
}
