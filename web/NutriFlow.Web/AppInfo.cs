namespace NutriFlow.Web;

public sealed class AppInfo
{
    public const string ProductionChannel = "production";

    public AppInfo(Version version, string? channel)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
        // anything but an explicit production build is treated as the test stand
        IsTest = !string.Equals(channel, ProductionChannel, StringComparison.Ordinal);
    }

    public Version Version { get; }

    public bool IsTest { get; }

    public string VersionText => IsTest
        ? $"Версия {Version.ToString(3)} · веб · тестовая"
        : $"Версия {Version.ToString(3)} · веб";
}
