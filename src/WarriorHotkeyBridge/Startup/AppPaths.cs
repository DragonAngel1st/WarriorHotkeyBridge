namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Every writable location the application uses.
/// </summary>
/// <remarks>
/// Nothing mutable is ever written next to the executable. The installed binaries live in a
/// directory the user may not be able to write to (and which an MSI upgrade replaces
/// wholesale), so logs, user configuration and state all live under %LOCALAPPDATA% where they
/// survive upgrades and uninstall.
/// </remarks>
internal sealed class AppPaths
{
    public const string ProductFolderName = "WarriorHotkeyBridge";

    private AppPaths(string root, string installDirectory)
    {
        Root = root;
        InstallDirectory = installDirectory;
        Logs = Path.Combine(root, "Logs");
        Configuration = Path.Combine(root, "Configuration");
        State = Path.Combine(root, "State");
        Diagnostics = Path.Combine(root, "Diagnostics");
        ChromeProfile = Path.Combine(root, "ChromeProfile");
    }

    /// <summary>%LOCALAPPDATA%\WarriorHotkeyBridge</summary>
    public string Root { get; }

    /// <summary>Directory the executable was loaded from. Treated as read-only.</summary>
    public string InstallDirectory { get; }

    public string Logs { get; }

    /// <summary>Holds the optional user appsettings.json that overrides the shipped defaults.</summary>
    public string Configuration { get; }

    public string State { get; }

    public string Diagnostics { get; }

    /// <summary>Default dedicated Chrome profile, used when configuration leaves it blank.</summary>
    public string ChromeProfile { get; }

    /// <summary>The user-editable configuration file. May legitimately not exist.</summary>
    public string UserConfigFile => Path.Combine(Configuration, "appsettings.json");

    public string LogFilePathTemplate => Path.Combine(Logs, "bridge-.log");

    /// <summary>
    /// Resolves the paths and creates the directories. Called before logging is configured,
    /// because the log directory has to exist first.
    /// </summary>
    public static AppPaths CreateAndEnsure()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        var paths = new AppPaths(
            root: Path.Combine(localAppData, ProductFolderName),
            installDirectory: AppContext.BaseDirectory);

        Directory.CreateDirectory(paths.Logs);
        Directory.CreateDirectory(paths.Configuration);
        Directory.CreateDirectory(paths.State);
        Directory.CreateDirectory(paths.Diagnostics);

        return paths;
    }
}
