using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Startup;

internal enum StartupState
{
    /// <summary>No Run entry: the bridge will not start at sign-in.</summary>
    Disabled,

    /// <summary>Registered and Windows will run it.</summary>
    Enabled,

    /// <summary>Registered, but switched off in Task Manager's Startup apps.</summary>
    BlockedByWindows,

    /// <summary>Registered, but pointing at a different executable - typically an old install.</summary>
    PointsElsewhere,

    /// <summary>The registry could not be read.</summary>
    Unknown,
}

internal sealed record StartupStatus(StartupState State, string? RegisteredCommand, string ExpectedCommand)
{
    public bool WillStartAtSignIn => State is StartupState.Enabled;

    public string Describe() => State switch
    {
        StartupState.Enabled => "enabled",
        StartupState.Disabled => "disabled",
        StartupState.BlockedByWindows => "registered but switched off in Task Manager > Startup apps",
        StartupState.PointsElsewhere => $"registered for a different executable ({RegisteredCommand})",
        _ => "unknown (registry could not be read)",
    };
}

internal interface IStartupManager
{
    StartupStatus GetStatus();

    bool TryEnable(out string? error);

    bool TryDisable(out string? error);
}

/// <summary>
/// Starts the bridge when the user signs in, via the per-user Run key.
/// </summary>
/// <remarks>
/// <para>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> is used rather than a Startup
/// folder shortcut, a scheduled task, or a service. It needs no administrator rights, it is
/// per-user so it cannot affect anyone else on the machine, Task Manager shows it where users
/// expect to find it, and it is a single value to add or remove. A Windows service was never an
/// option: session 0 cannot receive global hotkeys, reach the user's Chrome, or show a tray icon.
/// </para>
/// </remarks>
internal sealed class StartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager records whether a Run entry is switched on.</summary>
    private const string ApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "WarriorHotkeyBridge";

    /// <summary>First byte of the approval blob meaning "disabled". Observed 0x02 for enabled.</summary>
    private const byte DisabledFlag = 0x03;

    private readonly ILogger<StartupManager> _logger;

    public StartupManager(ILogger<StartupManager> logger) => _logger = logger;

    /// <summary>
    /// The executable Windows should launch.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> rather than the assembly location: for a
    /// self-contained publish those differ, and the apphost is the thing that must be launched.
    /// </remarks>
    private static string ExecutablePath => Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    public StartupStatus GetStatus()
    {
        string expected = StartupCommand.Format(ExecutablePath);

        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = runKey?.GetValue(ValueName) as string;

            if (string.IsNullOrWhiteSpace(command))
            {
                return new StartupStatus(StartupState.Disabled, null, expected);
            }

            if (!StartupCommand.PointsAt(command, ExecutablePath))
            {
                return new StartupStatus(StartupState.PointsElsewhere, command, expected);
            }

            return new StartupStatus(
                IsApprovedByWindows() ? StartupState.Enabled : StartupState.BlockedByWindows,
                command,
                expected);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.StartupRegistryFailed(ex.Message);
            return new StartupStatus(StartupState.Unknown, null, expected);
        }
    }

    /// <summary>
    /// Whether Task Manager has switched the entry off.
    /// </summary>
    /// <remarks>
    /// A Run value that exists is not the same as one that runs. Turning an app off under
    /// Startup apps leaves the value in place and records the decision here instead, so without
    /// this check the tray would cheerfully report "enabled" for something Windows will never
    /// launch. No entry at all means it has never been toggled, which means enabled.
    /// </remarks>
    private static bool IsApprovedByWindows()
    {
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false);

        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || flags[0] != DisabledFlag;
    }

    public bool TryEnable(out string? error)
    {
        try
        {
            using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            runKey.SetValue(ValueName, StartupCommand.Format(ExecutablePath), RegistryValueKind.String);

            _logger.StartupEnabled(ExecutablePath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.StartupRegistryFailed(ex.Message);
            error = ex.Message;
            return false;
        }
    }

    public bool TryDisable(out string? error)
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            // Only ever the value this application owns. The Run key is shared with everything
            // else the user has installed, and Task Manager's approval record is Windows' to
            // manage - deleting from it would be reaching into another application's state.
            runKey?.DeleteValue(ValueName, throwOnMissingValue: false);

            _logger.StartupDisabled();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.StartupRegistryFailed(ex.Message);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes every trace of startup registration. Called by the uninstaller, not by the tray.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static and dependency-free because it runs from <c>--uninstall-cleanup</c>, long before
    /// there is a host, a logger or a configuration file - and possibly while the MSI is midway
    /// through removing the very files a host would need.
    /// </para>
    /// <para>
    /// Unlike <see cref="TryDisable"/> this also clears Task Manager's approval record. Refusing
    /// to touch that record is right when merely switching startup off, because the record is
    /// Windows' own state about an application that still exists. Once the application is being
    /// removed the entry is simply litter naming a program that is gone, and it is keyed by a
    /// value name we own, so removing it cannot affect anything else.
    /// </para>
    /// <para>
    /// User configuration, logs, the startup preference and the dedicated Chrome profile are all
    /// left in place. They live under %LOCALAPPDATA% and belong to the operator; an uninstaller
    /// silently deleting a browser profile would be destroying data the MSI never created.
    /// </para>
    /// </remarks>
    public static bool RemoveRegistrationForUninstall(out string? error)
    {
        try
        {
            using (RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            using (RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
            {
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
