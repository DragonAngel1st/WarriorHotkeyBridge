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

    /// <summary>
    /// Switch the bridge on: register the hotkeys and bring Chrome up.
    /// </summary>
    /// <remarks>
    /// Equivalent to launching with no flags, and named anyway so a Stream Deck button says what
    /// it does. A bare second launch has always meant "ready the session"; keeping that means an
    /// existing Go Trading shortcut still works after this change.
    /// </remarks>
    public bool Start { get; init; }

    /// <summary>
    /// Switch the bridge off: release the hotkeys and close Chrome, leaving it resident.
    /// </summary>
    /// <remarks>
    /// Deliberately not spelled <c>--stop</c>, which is an existing alias for <c>--quit</c> and
    /// ends the process. Reusing it would silently change what every shortcut already using it
    /// does, including the one the installer wrote.
    /// </remarks>
    public bool Park { get; init; }

    /// <summary>
    /// Start resident but switched off, waiting to be armed.
    /// </summary>
    /// <remarks>
    /// What the sign-in registration uses. Starting armed at sign-in is what forced Chrome open
    /// every morning; starting parked means the operator decides when a session begins. A manual
    /// launch still arms, because someone who double-clicks the application wants to use it.
    /// </remarks>
    public bool StartParked { get; init; }

    /// <summary>
    /// Clear the session state - Chrome profile, logs, diagnostics, startup preference - and exit.
    /// </summary>
    /// <remarks>
    /// Never touches the configuration file or the presets folder. Run by the installer before it
    /// starts the upgraded application, so "reinstall it cleanly" is one action rather than a set
    /// of instructions relayed down a telephone.
    /// </remarks>
    public bool Reset { get; init; }

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
        bool start = false;
        bool park = false;
        bool startParked = false;
        bool reset = false;
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
                case "start":
                case "arm":
                case "go":
                    start = true;
                    continue;
                case "park":
                case "off":
                    park = true;
                    continue;
                case "reset":
                case "clean":
                    reset = true;
                    continue;
                case "parked":
                    startParked = true;
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
            Start = start,
            Park = park,
            StartParked = startParked,
            Reset = reset,
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
          --start, --go          Switch the bridge on: register the hotkeys and bring Chrome up.
                                 Starts the bridge first if it is not already resident. Same as
                                 launching with no options; pressing it twice is harmless.
          --park, --off          Switch the bridge off: release the hotkeys and close Chrome,
                                 leaving the tray icon running. Does nothing if not resident.
          --reset, --clean       Clear the Chrome profile, logs, diagnostics and startup
                                 preference, then exit. Your hotkeys and presets are NOT touched,
                                 and the bindings are snapshotted into presets first.
          --parked               Start resident but switched off. Used by the sign-in
                                 registration so a session begins only when you ask for one.
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
