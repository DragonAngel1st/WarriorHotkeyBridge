using System.Runtime.InteropServices;
using System.Text;

namespace WarriorHotkeyBridge.Diagnostics;

/// <summary>
/// Gives the GUI-subsystem executable a real console when <c>--debug</c> is passed.
/// </summary>
/// <remarks>
/// <para>
/// A PE image's subsystem (GUI vs console) is fixed at link time and .NET exposes no
/// supported way to change it at runtime, so a single binary cannot simply "become" a
/// console app. Attaching a console through Win32 is the standard solution and keeps us to
/// one executable, one code path and one set of shortcuts.
/// </para>
/// <para>
/// We prefer <c>AttachConsole(ATTACH_PARENT_PROCESS)</c> so that running the app from an
/// existing terminal logs into that terminal, and only fall back to <c>AllocConsole</c> -
/// which creates a new window - when there is no parent console, as with a Start Menu
/// shortcut.
/// </para>
/// </remarks>
internal static partial class ConsoleHost
{
    /// <summary>Special process id meaning "the console of the parent process".</summary>
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static bool _ownsConsoleWindow;

    /// <summary>
    /// True once a console is available. The console log sink is only wired up when this is
    /// true, otherwise Serilog would be writing to an invalid handle.
    /// </summary>
    public static bool IsAttached { get; private set; }

    /// <summary>
    /// True when the console window belongs to this process (allocated by
    /// <c>AllocConsole</c>), false when it was inherited from the launching terminal.
    /// </summary>
    /// <remarks>
    /// This matters whenever the process is about to exit: Windows destroys a process-owned
    /// console along with the process, so a message written just before returning from Main
    /// flashes for a frame and is gone. An inherited console outlives us and can be read.
    /// Callers that report and exit must therefore fall back to a dialog when this is true.
    /// </remarks>
    public static bool OwnsConsoleWindow => _ownsConsoleWindow;

    /// <summary>
    /// Attaches or allocates a console and rebinds the managed standard streams to it.
    /// Safe to call more than once.
    /// </summary>
    /// <returns>True if a console is available afterwards.</returns>
    public static bool EnsureConsole() => EnsureConsole(allocateIfMissing: true);

    /// <summary>
    /// Attaches to the launching terminal, and optionally creates a console window if there is
    /// none.
    /// </summary>
    /// <param name="allocateIfMissing">
    /// False for commands that may run inside a silent installer. <c>AllocConsole</c> puts a
    /// window on screen, so a command invoked by <c>msiexec /qn</c> would make a supposedly
    /// silent install flash a black console - for as long as the command takes, which for a
    /// stop request is up to its full timeout. Attaching to an inherited console is always safe,
    /// because having one means somebody is already looking at a terminal.
    /// </param>
    public static bool EnsureConsole(bool allocateIfMissing)
    {
        if (IsAttached)
        {
            return true;
        }

        if (AttachConsole(AttachParentProcess))
        {
            _ownsConsoleWindow = false;
        }
        else if (allocateIfMissing && AllocConsole())
        {
            _ownsConsoleWindow = true;
        }
        else
        {
            // Nothing more to do: file logging still works, and the caller reports that the
            // console could not be shown rather than pretending debug mode is fully active.
            return false;
        }

        RebindStandardStreams();
        IsAttached = true;

        if (_ownsConsoleWindow)
        {
            TrySetConsoleAppearance();
        }

        return true;
    }

    /// <summary>
    /// Points <see cref="Console"/> at the console we just obtained.
    /// </summary>
    /// <remarks>
    /// <see cref="Console.Out"/> is created lazily and then cached. In a WinExe it is
    /// generally already bound to an invalid handle by the time we attach, which is why a
    /// naive AllocConsole produces a console window that stays blank forever. Re-opening the
    /// standard streams re-queries <c>GetStdHandle</c> and picks up the new console.
    /// </remarks>
    private static void RebindStandardStreams()
    {
        var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(standardOutput);

        var standardError = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(standardError);

        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    private static void TrySetConsoleAppearance()
    {
        try
        {
            Console.Title = "Warrior Hotkey Bridge - Debug Console";
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Console appearance is cosmetic. A redirected or unusually hosted console can
            // reject these; logging still works, so there is nothing to recover from.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);
}
