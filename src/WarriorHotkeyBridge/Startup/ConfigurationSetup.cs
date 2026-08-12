using Microsoft.Extensions.Configuration;

namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Builds the layered configuration used both by the bootstrap logger and by the host.
/// </summary>
internal static class ConfigurationSetup
{
    /// <summary>
    /// Applies the configuration sources, lowest precedence first.
    /// </summary>
    /// <remarks>
    /// The user override file under %LOCALAPPDATA% is the mechanism that lets an MSI upgrade
    /// replace the shipped appsettings.json without ever discarding the operator's hotkey
    /// mappings: shipped defaults are read-only, user edits live in a separate file, and the
    /// user file wins.
    /// </remarks>
    public static IConfigurationBuilder Apply(
        IConfigurationBuilder builder,
        AppPaths paths,
        IReadOnlyList<string> configurationArgs)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configurationArgs);

        builder.SetBasePath(paths.InstallDirectory);

        // 1. Shipped defaults, next to the executable.
        builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        // 2. User overrides that survive upgrade and uninstall.
        builder.AddJsonFile(paths.UserConfigFile, optional: true, reloadOnChange: false);

        // 3. Environment, e.g. WHB_Chrome__CdpEndpoint.
        builder.AddEnvironmentVariables(prefix: "WHB_");

        // 4. Explicit --Section:Key=value overrides.
        if (configurationArgs.Count > 0)
        {
            builder.AddCommandLine([.. configurationArgs]);
        }

        return builder;
    }
}
