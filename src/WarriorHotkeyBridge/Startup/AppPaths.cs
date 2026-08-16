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
        Presets = Path.Combine(root, "Presets");
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

    /// <summary>
    /// The operator's own hotkey presets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created by the application, deliberately never by the installer. An MSI that creates a
    /// directory also owns it, and an owned directory is one the installer may remove on uninstall
    /// or during the remove-then-install half of an upgrade. Presets are the operator's work and
    /// must outlive both, so the installer is kept away from this path entirely.
    /// </para>
    /// <para>
    /// Created eagerly rather than on first save, because it is a place the operator is told to
    /// put files - restoring a backup, or taking a layout from another machine. A folder that only
    /// appears once you have already saved a preset from the editor is no use to someone who has
    /// one and nowhere to put it.
    /// </para>
    /// </remarks>
    public string Presets { get; }

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

        return CreateAndEnsure(Path.Combine(localAppData, ProductFolderName));
    }

    /// <summary>
    /// Resolves the paths beneath an explicit root and creates the directories.
    /// </summary>
    /// <remarks>
    /// Exists so tests can operate on a temporary directory. The parameterless overload resolves
    /// the root through <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, which
    /// asks the Windows shell and deliberately ignores the LOCALAPPDATA environment variable - so
    /// a test that redirected that variable would silently keep operating on the real profile, and
    /// anything it wrote would land in the operator's live configuration.
    /// </remarks>
    public static AppPaths CreateAndEnsure(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var paths = new AppPaths(root, installDirectory: AppContext.BaseDirectory);

        Directory.CreateDirectory(paths.Logs);
        Directory.CreateDirectory(paths.Configuration);
        Directory.CreateDirectory(paths.State);
        Directory.CreateDirectory(paths.Diagnostics);

        // CreateDirectory is a no-op on an existing directory and never touches its contents, so
        // this is safe to run on every start against a folder full of the operator's presets.
        Directory.CreateDirectory(paths.Presets);

        return paths;
    }
}
