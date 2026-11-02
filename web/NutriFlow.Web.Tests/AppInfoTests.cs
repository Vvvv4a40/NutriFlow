namespace NutriFlow.Web.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void ProductionChannelShowsPlainWebVersion()
    {
        AppInfo info = new(new Version(0, 2, 0, 0), "production");

        Assert.False(info.IsTest);
        Assert.Equal("Версия 0.2.0 · веб", info.VersionText);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("Production")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherChannelIsMarkedAsTestStand(string? channel)
    {
        AppInfo info = new(new Version(0, 2, 0), channel);

        Assert.True(info.IsTest);
        Assert.Equal("Версия 0.2.0 · веб · тестовая", info.VersionText);
    }
}
