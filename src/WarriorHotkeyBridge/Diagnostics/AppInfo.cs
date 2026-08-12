using System.Reflection;

namespace WarriorHotkeyBridge.Diagnostics;

/// <summary>
/// Identity of the running build, read from the assembly so there is exactly one place
/// (Directory.Build.props) where the version is defined.
/// </summary>
internal static class AppInfo
{
    public const string ProductName = "Warrior Hotkey Bridge";

    /// <summary>Informational version, e.g. "1.0.0" or "1.0.0+abc1234" for a stamped build.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Version without any build metadata suffix, for display in the tray menu.</summary>
    public static string DisplayVersion { get; } = Version.Split('+', 2)[0];

    /// <summary>
    /// This process's name as Windows reports it, for finding sibling instances.
    /// </summary>
    /// <remarks>
    /// Taken from the running process rather than hard-coded, so a renamed or repackaged
    /// executable still finds its own instances instead of silently matching nothing.
    /// </remarks>
    public static string ProcessName { get; } =
        System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    public static string RuntimeVersion => Environment.Version.ToString();

    public static string FrameworkDescription =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
