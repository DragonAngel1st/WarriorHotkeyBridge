using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Diagnostics;

/// <summary>
/// Builds the Serilog pipeline: a rolling file always, plus a console in debug mode.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>Matches the timestamped, level-tagged format used throughout the docs.</summary>
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static Logger Create(LogOptions options, AppPaths paths, bool debugMode)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        // Debug mode always wins over configuration: the point of --debug is to see everything.
        LogEventLevel minimumLevel = debugMode
            ? LogEventLevel.Debug
            : ParseLevel(options.MinimumLevel);

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: paths.LogFilePathTemplate,
                outputTemplate: FileTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                // Unbuffered: a crash or a killed debug console must not lose the entries that
                // explain why. The write volume here is far too low for buffering to matter.
                buffered: false);

        if (debugMode && ConsoleHost.IsAttached)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                // Literate rather than an ANSI theme: it uses the console colour API and so
                // renders correctly in legacy conhost as well as Windows Terminal.
                theme: SystemConsoleTheme.Literate);
        }

        return configuration.CreateLogger();
    }

    private static LogEventLevel ParseLevel(string value) =>
        Enum.TryParse(value, ignoreCase: true, out LogEventLevel level)
            ? level
            : LogEventLevel.Information;
}
