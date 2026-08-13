using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Commands;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;
using WarriorHotkeyBridge.Startup;
using WarriorHotkeyBridge.Tray;
using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitAlreadyRunning = 2;

    /// <summary>
    /// <c>--quit</c> delivered the request but the instance had not exited in time. Distinct
    /// from success so a script can tell "stopped" from "probably stopping".
    /// </summary>
    private const int ExitShutdownTimedOut = 3;

    private const int ExitUsageError = 64;

    /// <summary>
    /// Generous relative to a normal shutdown, which is milliseconds. The slow case is a
    /// <c>--close-chrome</c> stop whose CDP round trip is waiting on a busy page; host shutdown
    /// itself is capped at 10 seconds, so this sits just beyond it.
    /// </summary>
    private static readonly TimeSpan QuitWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a stop request waits for a starting instance to publish its shutdown events.
    /// </summary>
    /// <remarks>
    /// Bounded by how long startup takes to get from the instance mutex to those events, which
    /// is a few instructions. Seconds of headroom covers a machine still thrashing through its
    /// sign-in workload, which is precisely when this race is reachable.
    /// </remarks>
    private static readonly TimeSpan StartupRaceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// STAThread is required: WinForms, the notification area and the clipboard all demand a
    /// single-threaded apartment. It also has to be a synchronous entry point - the C#
    /// compiler does not carry <c>[STAThread]</c> onto the generated wrapper for an
    /// <c>async Main</c>, so the attribute would silently do nothing.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLineOptions cli = CommandLineOptions.Parse(args);

        if (cli.UnknownArgs.Count > 0)
        {
            ConsoleHost.EnsureConsole();
            Console.Error.WriteLine($"Unrecognised argument(s): {string.Join(", ", cli.UnknownArgs)}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineOptions.HelpText);
            return ExitUsageError;
        }

        if (cli.ShowHelp)
        {
            ConsoleHost.EnsureConsole();
            Console.WriteLine(CommandLineOptions.HelpText);
            return ExitSuccess;
        }

        if (cli.ShowVersion)
        {
            ConsoleHost.EnsureConsole();
            Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version} ({AppInfo.FrameworkDescription})");
            return ExitSuccess;
        }

        // Handled before the instance guard, and before any logging: the uninstaller runs this
        // while it is removing the application's own files, so nothing here may depend on the
        // install directory still being intact.
        if (cli.UninstallCleanup)
        {
            ConsoleHost.EnsureConsole(allocateIfMissing: !cli.Silent);

            bool removed = StartupManager.RemoveRegistrationForUninstall(out string? cleanupError);

            WriteLineIfConsole(removed
                ? "Startup registration removed."
                : $"Could not remove startup registration: {cleanupError}");

            // Never fail the uninstall over this. A leftover Run value points at a path that no
            // longer exists, which Windows ignores at logon; blocking removal of the whole
            // product would be far worse than that.
            return ExitSuccess;
        }

        // Handled before the instance guard: this process is not trying to become the bridge,
        // it is asking the existing one to stop. Taking the mutex would make it look like a
        // rejected second instance.
        if (cli.Quit)
        {
            ConsoleHost.EnsureConsole(allocateIfMissing: !cli.Silent);

            bool signalled = SignalShutdown(cli.CloseChrome);

            if (!signalled)
            {
                // Not an error: a stop button should not report failure because the thing was
                // already stopped.
                WriteLineIfConsole("Warrior Hotkey Bridge is not running.");
                return ExitSuccess;
            }

            WriteLineIfConsole(cli.CloseChrome
                ? "Asked the running Warrior Hotkey Bridge to exit and close Chrome."
                : "Asked the running Warrior Hotkey Bridge to exit.");

            // Signalling only delivers the request; the instance still has to unregister its
            // hotkeys, drain the queue and flush the log. Waiting turns "asked" into "done",
            // which is what both the installer and a Stream Deck stop button actually need.
            if (WaitForShutdown(QuitWaitTimeout))
            {
                WriteLineIfConsole("It has exited.");
                return ExitSuccess;
            }

            ReportQuitTimedOut(cli.Silent);
            return ExitShutdownTimedOut;
        }

        bool consoleAvailable = !cli.Debug || ConsoleHost.EnsureConsole();

        // The instance guard runs before logging is configured, not just as a courtesy: the
        // rolling file sink opens the log exclusively, so a second instance would fail on the
        // log file rather than on the thing that actually matters.
        using SingleInstanceGuard instance = SingleInstanceGuard.Acquire();

        if (!instance.IsPrimary)
        {
            // A bridge is already resident, which is not a problem to report - it is most of what
            // this launch wanted. Hand the request over instead: the running instance brings
            // Chrome up if it is not there, so pressing "go trading" is idempotent and always
            // ends in a session that is ready, whatever state things were in.
            if (ActivationSignal.TrySignal())
            {
                WriteLineIfConsole("Warrior Hotkey Bridge is already running; asked it to ready the session.");
                return ExitSuccess;
            }

            // Could not hand it over: the instance released the slot between the two calls, or is
            // wedged. Rare enough to be worth saying out loud.
            ReportAlreadyRunning(cli.Silent);
            return ExitAlreadyRunning;
        }

        // Published here, immediately after the slot is taken, rather than once the host exists.
        // Everything between this line and Application.Run - configuration, logging, Playwright -
        // is time during which another process asks "is the bridge running?" by trying to open
        // these events. Creating them late meant a fully live instance answered "no" for the
        // whole of its own startup, which is how an upgrade came to replace files that were
        // still mapped. The handler is attached later, and a request that arrives first is held.
        using ShutdownSignal shutdown = ShutdownSignal.Create();

        // Same reasoning, same moment: a second launch decides what to do by whether this event
        // exists, so it has to exist for the whole of startup rather than appearing at the end.
        using ActivationSignal activation = ActivationSignal.Create();

        AppPaths paths;
        IConfigurationRoot configuration;
        bool starterConfigurationWritten = false;

        // Bootstrap is guarded separately because none of it can be logged: Log.Logger is still
        // Serilog's silent no-op logger until the last line of this block. Every step here can
        // throw on ordinary user input - a hand-edited user appsettings.json with a trailing
        // comma makes ConfigurationBuilder.Build throw InvalidDataException regardless of the
        // file being marked optional, a non-numeric Log value makes the binder throw, and a
        // RetainedFileCountLimit of 0 makes the Serilog file sink throw. Without this catch the
        // exception escapes Main: no tray icon, no dialog, nothing in the log the user is told
        // to check, and a CLR crash exit code instead of ExitFailure.
        try
        {
            paths = AppPaths.CreateAndEnsure();

            // Before the configuration is built, so a first run loads the file it just wrote and
            // the operator can see it take effect rather than having to restart. Safe to load
            // because every binding in the template is an example held outside the Bindings
            // object: as written the file changes nothing.
            starterConfigurationWritten = StarterConfiguration.TryWrite(paths);

            configuration = BuildConfiguration(paths, cli);
            LogOptions logOptions = configuration.GetSection(LogOptions.SectionName).Get<LogOptions>() ?? new LogOptions();
            Log.Logger = LoggingSetup.Create(logOptions, paths, cli.Debug);
        }
        catch (Exception ex)
        {
            ReportBootstrapFailure(ex, cli.Debug);
            return ExitFailure;
        }

        try
        {
            Log.Information("{Product} {Version} starting.", AppInfo.ProductName, AppInfo.Version);
            Log.Information("Mode: {Mode}.", cli.Debug ? "debug (console + verbose logging)" : "normal (tray only)");
            Log.Information("Runtime: {Runtime}.", AppInfo.FrameworkDescription);
            Log.Information("Install directory: {InstallDirectory}", paths.InstallDirectory);
            Log.Information("Log directory: {LogDirectory}", paths.Logs);
            Log.Debug("User configuration file: {UserConfig} (exists: {Exists})",
                paths.UserConfigFile, File.Exists(paths.UserConfigFile));

            if (starterConfigurationWritten)
            {
                Log.Information(
                    "First run: wrote a commented example configuration to {UserConfig}. "
                    + "Nothing in it is active yet - it explains the format and lists examples to copy "
                    + "into the Bindings section. F23 (Test) and F24 (Diagnostics) work now and send nothing.",
                    paths.UserConfigFile);
            }

            if (cli.Debug && !consoleAvailable)
            {
                Log.Warning("Debug mode was requested but no console could be attached; file logging only.");
            }

            return Run(paths, configuration, cli, shutdown, activation);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed; the bridge is exiting.");

            if (!cli.Debug)
            {
                // With no console there is nothing to read, so say where the detail lives.
                MessageBox.Show(
                    $"{AppInfo.ProductName} could not start.\n\n{ex.Message}\n\nSee the log at:\n{paths.Logs}",
                    AppInfo.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return ExitFailure;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Delivers a stop request, tolerating an instance that is still starting up.
    /// </summary>
    /// <remarks>
    /// The shutdown events are published immediately after the instance mutex is taken, so the
    /// window in which a live instance has the mutex but not yet the events is a few
    /// instructions wide. It is not zero, though, and losing that race is expensive: the caller
    /// is told "not running", and the installer then replaces files the bridge still has mapped.
    /// So when the events are absent but the instance slot is taken, this waits for them rather
    /// than concluding nothing is there.
    /// </remarks>
    private static bool SignalShutdown(bool closeChrome)
    {
        if (ShutdownSignal.TrySignal(closeChrome))
        {
            return true;
        }

        long deadline = Environment.TickCount64 + (long)StartupRaceTimeout.TotalMilliseconds;

        // A free slot is proof nothing is running, which is the ordinary case and returns at once.
        while (!SingleInstanceGuard.WaitUntilFree(TimeSpan.Zero))
        {
            if (ShutdownSignal.TrySignal(closeChrome))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>
    /// Waits for the instance to finish shutting down and for its process to actually exit.
    /// </summary>
    /// <remarks>
    /// Releasing the mutex proves the orderly shutdown ran to completion, but it happens in a
    /// finally block - the process still has to unwind and have its image unmapped afterwards.
    /// The installer replaces that image, so the residual gap is exactly the files-in-use
    /// condition this is meant to avoid. Waiting on the process handle as well closes it.
    /// </remarks>
    private static bool WaitForShutdown(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        if (!SingleInstanceGuard.WaitUntilFree(timeout))
        {
            return false;
        }

        int self = Environment.ProcessId;

        foreach (Process process in Process.GetProcessesByName(AppInfo.ProcessName))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }

                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);

                try
                {
                    if (!process.WaitForExit(remaining))
                    {
                        return false;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                {
                    // Already gone, or not ours to wait on. Either way there is nothing left to
                    // wait for, and failing the stop over it would be wrong.
                }
            }
        }

        return true;
    }

    private static void ReportQuitTimedOut(bool silent)
    {
        string message =
            $"{AppInfo.ProductName} did not exit within {QuitWaitTimeout.TotalSeconds:0} seconds. "
            + "It may still be shutting down, and its hotkeys may still be registered.";

        if (ConsoleHost.IsAttached)
        {
            Console.Error.WriteLine(message);
        }

        // A console this process allocated dies with it, so the message would flash for a frame
        // and vanish - and the operator would believe the hotkeys were released when they were
        // not. Silent mode still says nothing: an unattended installer has nobody to read a
        // dialog, and would simply hang on it.
        if (!silent && (!ConsoleHost.IsAttached || ConsoleHost.OwnsConsoleWindow))
        {
            MessageBox.Show(message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void WriteLineIfConsole(string message)
    {
        if (ConsoleHost.IsAttached)
        {
            Console.WriteLine(message);
        }
    }

    private static int Run(
        AppPaths paths,
        IConfigurationRoot configuration,
        CommandLineOptions cli,
        ShutdownSignal shutdown,
        ActivationSignal activation)
    {
        ApplicationConfiguration.Initialize();

        // Must precede the creation of any window, including the dispatcher's anchor control.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = AppPaths.ProductFolderName,

            // Explicit, because the default is the process working directory. A tray app
            // launched from a Start Menu shortcut or from Run-at-login inherits an
            // unpredictable working directory, which would break relative config loading.
            ContentRootPath = paths.InstallDirectory,

            // Command-line configuration is applied via our own filtered source instead; the
            // built-in provider throws on bare flags such as --debug.
            Args = null,
        });

        builder.Configuration.AddConfiguration(configuration);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();

        ConfigureServices(builder.Services, builder.Configuration, paths, cli);

        using IHost host = builder.Build();

        // Resolved here, on the UI thread, so the anchor control's window handle belongs to
        // the thread that will run the message loop.
        _ = host.Services.GetRequiredService<IUiDispatcher>();

        InstallExceptionHandlers(host.Services);

        host.Start();

        IBridgeStateService state = host.Services.GetRequiredService<IBridgeStateService>();
        var context = host.Services.GetRequiredService<TrayApplicationContext>();

        // Lets a Stream Deck "stop trading" button end the session cleanly, releasing the
        // hotkeys for other applications, rather than the button having to kill the process.
        // The events themselves went live back in Main; this supplies what to do about them,
        // and runs immediately if a request already arrived during startup.
        shutdown.Attach(closeChrome =>
        {
            Log.Information("Shutdown requested by another instance (--quit, closeChrome={CloseChrome}).", closeChrome);

            if (closeChrome)
            {
                // Closed before the host stops, while the CDP connection is still live. Waiting
                // is safe here: this runs on a thread-pool thread, not the UI thread.
                host.Services.GetRequiredService<IChromeConnectionManager>()
                    .CloseBrowserAsync()
                    .GetAwaiter()
                    .GetResult();
            }

            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        });

        // A second launch - the operator pressing "go trading" again - lands here instead of
        // being turned away. Bringing Chrome up is the useful half of what that press wanted.
        activation.Attach(() =>
        {
            Log.Information("Another launch asked for the session to be readied.");

            try
            {
                // Thread-pool thread, so blocking is safe; the UI thread is untouched.
                bool ready = host.Services.GetRequiredService<IChromeLauncher>()
                    .LaunchOnRequestAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (!ready)
                {
                    Log.Warning(
                        "Could not ready the session. Chrome is not answering on the configured "
                        + "endpoint, and Chrome:AutoLaunch is off so the bridge will not start it.");
                }
            }
            catch (Exception ex)
            {
                // Never let a button press take the bridge down; the hotkeys matter more.
                Log.Error(ex, "Readying the session failed.");
            }
        });

        state.Update(current => current with { Application = ApplicationState.Running });

        // Registered here, on the UI thread, and before the loop starts: Win32 binds a hotkey
        // to the registering thread's window, and WM_HOTKEY is only ever delivered to that
        // thread's queue. Doing this from a hosted service would register on a thread-pool
        // thread, succeed, and then never deliver a single keypress.
        var hotkeys = host.Services.GetRequiredService<GlobalHotkeyService>();
        hotkeys.RegisterAll();

        Log.Information("Bridge running. State: {State}", state.Current.ToLogSummary());

        Application.Run(context);

        Log.Information("Message loop ended; stopping host.");
        state.Update(current => current with { Application = ApplicationState.ShuttingDown });

        // Still the UI thread, and the hotkey window still exists. Releasing here rather than
        // in Dispose keeps unregistration on the thread Win32 requires.
        hotkeys.UnregisterAll();

        // Blocking here is correct and safe: the message loop has already exited, so there is
        // no UI thread left to starve, and host shutdown does not post back to it.
        host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

        Log.Information("Shutdown complete.");
        return ExitSuccess;
    }

    private static void ConfigureServices(IServiceCollection services, ConfigurationManager configuration, AppPaths paths, CommandLineOptions cli)
    {
        services.AddSingleton(paths);
        services.AddSingleton(cli);

        // Validated at startup so a malformed endpoint or selector list is reported once,
        // clearly, instead of failing later inside the command path.
        services.AddOptions<ChromeOptions>()
            .Bind(configuration.GetSection(ChromeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<WarriorSimOptions>()
            .Bind(configuration.GetSection(WarriorSimOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<HotkeyOptions>()
            .Bind(configuration.GetSection(HotkeyOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<CommandOptions>()
            .Bind(configuration.GetSection(CommandOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // A faulted background service must degrade the bridge, never end it. The default
        // behaviour stops the host, which would end the message loop and silently remove the
        // tray icon mid-session, leaving the operator's hotkeys quietly dead.
        services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        services.AddOptions<LogOptions>()
            .Bind(configuration.GetSection(LogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Replaces the default ConsoleLifetime; the message loop owns this process.
        services.AddSingleton<IHostLifetime, TrayHostLifetime>();

        services.AddSingleton<WinFormsUiDispatcher>();
        services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<WinFormsUiDispatcher>());
        services.AddSingleton<IBridgeStateService, BridgeStateService>();

        services.AddSingleton<IChromeLauncher, ChromeLauncher>();
        services.AddSingleton<ChromeConnectionManager>();
        services.AddSingleton<IChromeConnectionManager>(sp => sp.GetRequiredService<ChromeConnectionManager>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<IStartupManager, StartupManager>();
        services.AddSingleton<IStartupPreferenceStore, StartupPreferenceStore>();
        services.AddSingleton<IStartupPrompt, MessageBoxStartupPrompt>();

        // Registers Start with Windows on first run only; never overrides a later choice.
        services.AddHostedService<StartupRegistrationService>();
        services.AddSingleton<ILevel2Controller, Level2Controller>();
        services.AddSingleton<IWarriorPageLocator, WarriorPageLocator>();
        services.AddSingleton<IPageActivator, ChromeWindowActivator>();
        services.AddSingleton<IHotkeyActionExecutor, HotkeyActionExecutor>();
        services.AddSingleton<CommandQueue>();

        // The single consumer. Registered as a hosted service so its lifetime is the host's,
        // and so shutdown drains the queue before Playwright is disposed.
        services.AddHostedService<CommandDispatcher>();

        services.AddSingleton<SystemResumeMonitor>();
        services.AddSingleton<ISystemResumeMonitor>(sp => sp.GetRequiredService<SystemResumeMonitor>());

        // Runs on the thread pool: Chrome connection work must never touch the UI thread.
        services.AddHostedService<ChromeConnectionWorker>();

        // Reclaims hotkeys that another application was holding when the bridge started.
        services.AddHostedService<HotkeyRetryWorker>();

        services.AddSingleton<IHotkeyBindingStore, HotkeyBindingStore>();
        services.AddSingleton<IUserConfigurationWriter, UserConfigurationWriter>();
        services.AddSingleton<IHotkeyPresetProvider, HotkeyPresetProvider>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<IGlobalHotkeyService>(sp => sp.GetRequiredService<GlobalHotkeyService>());

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<TrayApplicationContext>();
    }

    private static IConfigurationRoot BuildConfiguration(AppPaths paths, CommandLineOptions cli)
    {
        var builder = new ConfigurationBuilder();
        ConfigurationSetup.Apply(builder, paths, cli.ConfigurationArgs);
        return builder.Build();
    }

    /// <summary>
    /// A resident tray application must outlive routine faults. Everything here is logged and
    /// surfaced in the tray rather than swallowed, but it does not tear the process down.
    /// </summary>
    private static void InstallExceptionHandlers(IServiceProvider services)
    {
        ILogger<TrayApplicationContext> logger = services.GetRequiredService<ILogger<TrayApplicationContext>>();
        IBridgeStateService state = services.GetRequiredService<IBridgeStateService>();

        Application.ThreadException += (_, e) =>
        {
            logger.UiThreadException(e.Exception);
            state.Update(current => current with { LastError = e.Exception.Message });
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Terminating is true for exceptions the runtime cannot recover from; flush so the
            // log explains the disappearance.
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating}).", e.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.UnobservedTaskException(e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Reports a rejected second instance, unless asked to stay quiet.
    /// </summary>
    /// <remarks>
    /// The dialog is right for someone who double-clicked a shortcut and is owed an explanation.
    /// It is wrong for a Stream Deck "go trading" button, where pressing an already-pressed
    /// button is a normal thing to do and the correct response is to carry on silently - the
    /// bridge is running, which is exactly what the button asked for. The exit code still
    /// distinguishes the two for anything that cares.
    /// </remarks>
    private static void ReportAlreadyRunning(bool silent)
    {
        if (silent)
        {
            return;
        }

        const string Message =
            "Warrior Hotkey Bridge is already running.\n\n" +
            "Only one instance may run at a time, because two would compete to register the " +
            "same global hotkeys. Exit the running instance from its notification-area icon first.";

        ReportBeforeLogging(Message, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Reports a failure that happened before the logger existed.
    /// </summary>
    /// <remarks>
    /// Recomputes the expected paths from the environment rather than from
    /// <see cref="AppPaths"/>, because resolving those paths may be the thing that just failed.
    /// </remarks>
    private static void ReportBootstrapFailure(Exception exception, bool debugMode)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            AppPaths.ProductFolderName);

        string message =
            $"{AppInfo.ProductName} could not start.\n\n" +
            $"{exception.GetType().Name}: {exception.Message}\n\n" +
            "This is usually a malformed configuration file. Check:\n" +
            $"{Path.Combine(root, "Configuration", "appsettings.json")}\n\n" +
            $"Logs (if any): {Path.Combine(root, "Logs")}";

        if (debugMode)
        {
            message += $"\n\n{exception}";
        }

        ReportBeforeLogging(message, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Shows a message on the way out, before any logging exists.
    /// </summary>
    private static void ReportBeforeLogging(string message, MessageBoxIcon icon)
    {
        string forConsole = message.Replace("\n", Environment.NewLine, StringComparison.Ordinal);

        if (ConsoleHost.IsAttached)
        {
            Console.Error.WriteLine(forConsole);
        }

        // A console this process allocated is destroyed the moment Main returns, so it can
        // never be the only channel - otherwise the user sees a black window blink and nothing
        // else. Only an inherited terminal survives to be read.
        if (!ConsoleHost.IsAttached || ConsoleHost.OwnsConsoleWindow)
        {
            MessageBox.Show(message, AppInfo.ProductName, MessageBoxButtons.OK, icon);
        }
    }
}
