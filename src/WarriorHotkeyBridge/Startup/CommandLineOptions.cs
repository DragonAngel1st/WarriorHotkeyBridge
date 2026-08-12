namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Parsed command line. Deliberately hand-rolled rather than taking a parser dependency:
/// the surface is three flags plus configuration pass-through.
/// </summary>
internal sealed record CommandLineOptions
{
    /// <summary>Show a console window and lower the log level to Debug.</summary>
    public bool Debug { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    /// <summary>
    /// Ask a running instance to exit, then exit. Used by a Stream Deck "stop trading" button.
    /// </summary>
    public bool Quit { get; init; }

    /// <summary>With <see cref="Quit"/>, also close the dedicated Chrome instance.</summary>
    public bool CloseChrome { get; init; }

    /// <summary>
    /// Remove startup registration and exit. Invoked by the uninstaller.
    /// </summary>
    /// <remarks>
    /// The application owns the Run value, so the application removes it. An MSI that wrote that
    /// value would own it instead, and every repair or upgrade would restore a setting the
    /// operator had deliberately switched off.
    /// </remarks>
    public bool UninstallCleanup { get; init; }

    /// <summary>
    /// Suppress all windows - no console, no dialogs. Passed by the installer.
    /// </summary>
    /// <remarks>
    /// A silent install must stay silent. Without this the stop and cleanup commands would show
    /// a console window, and a failed stop would show a dialog that nothing is there to dismiss,
    /// hanging an unattended install until its timeout.
    /// </remarks>
    public bool Silent { get; init; }

    /// <summary>
    /// Arguments handed to the configuration binder, e.g. <c>--Chrome:CdpEndpoint=http://127.0.0.1:9333</c>.
    /// Only <c>--Section:Key=value</c> forms are forwarded; the configuration command-line
    /// provider throws on bare flags, so ours are filtered out first.
    /// </summary>
    public IReadOnlyList<string> ConfigurationArgs { get; init; } = [];

    /// <summary>Anything unrecognised, reported to the user instead of being silently ignored.</summary>
    public IReadOnlyList<string> UnknownArgs { get; init; } = [];

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool debug = false;
        bool help = false;
        bool version = false;
        bool quit = false;
        bool closeChrome = false;
        bool uninstallCleanup = false;
        bool silent = false;
        List<string> configurationArgs = [];
        List<string> unknown = [];

        foreach (string arg in args)
        {
            string normalized = Normalize(arg);

            switch (normalized)
            {
                case "debug":
                case "d":
                    debug = true;
                    continue;
                case "help":
                case "h":
                case "?":
                    help = true;
                    continue;
                case "version":
                    version = true;
                    continue;
                case "quit":
                case "stop":
                case "exit":
                    quit = true;
                    continue;
                case "close-chrome":
                case "closechrome":
                    closeChrome = true;
                    continue;
                case "uninstall-cleanup":
                case "uninstallcleanup":
                    uninstallCleanup = true;
                    continue;
                case "silent":
                case "quiet":
                    silent = true;
                    continue;
            }

            // Configuration overrides must be fully qualified so we can tell them apart
            // from a mistyped flag: --Section:Key=value.
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=', StringComparison.Ordinal))
            {
                configurationArgs.Add(arg);
                continue;
            }

            unknown.Add(arg);
        }

        return new CommandLineOptions
        {
            Debug = debug,
            ShowHelp = help,
            ShowVersion = version,
            Quit = quit,
            CloseChrome = closeChrome,
            UninstallCleanup = uninstallCleanup,
            Silent = silent,
            ConfigurationArgs = configurationArgs,
            UnknownArgs = unknown,
        };
    }

    /// <summary>Strips the leading <c>--</c>, <c>-</c> or <c>/</c> and lower-cases the flag name.</summary>
    private static string Normalize(string arg)
    {
        ReadOnlySpan<char> span = arg.AsSpan();

        if (span.StartsWith("--", StringComparison.Ordinal))
        {
            span = span[2..];
        }
        else if (span.Length > 0 && (span[0] == '-' || span[0] == '/'))
        {
            span = span[1..];
        }

        return span.ToString().ToLowerInvariant();
    }

    public static string HelpText =>
        """
        Warrior Hotkey Bridge

        Routes global keyboard hotkeys (e.g. from a Stream Deck) to the Warrior Trading SIM
        page in Chrome over the DevTools Protocol. Runs as a Windows tray application.

        Usage:
          WarriorHotkeyBridge.exe [options]

        Options:
          --debug, -d            Show a diagnostic console window and log at Debug level.
          --version              Print the application version and exit.
          --quit, --stop         Ask a running instance to exit cleanly, then exit.
          --close-chrome         With --quit, also close the dedicated Chrome instance.
                                 Releases the global hotkeys for other applications.
          --uninstall-cleanup    Remove startup registration and exit. Used by the uninstaller;
                                 leaves configuration, logs and the Chrome profile untouched.
          --silent, --quiet      Show no console window and no dialogs. For unattended use;
                                 the exit code carries the result.
          --help, -h, -?         Show this help and exit.
          --Section:Key=value    Override any configuration value, e.g.
                                 --Chrome:CdpEndpoint=http://127.0.0.1:9333

        Only one instance may run at a time, in either mode: two instances would compete to
        register the same global hotkeys. Exit the running instance from its tray icon before
        starting another.
        """;
}
