using System.Runtime.InteropServices;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// Win32 entry points for global hotkey registration.
/// </summary>
/// <remarks>
/// <c>RegisterHotKey</c> is used in preference to a <c>WH_KEYBOARD_LL</c> hook on purpose. A
/// low-level hook sees every keystroke in the session, must respond within the system hook
/// timeout or be silently evicted, and is treated with suspicion by security software.
/// <c>RegisterHotKey</c> asks the OS for exactly the keys we want and receives them as an
/// ordinary posted message.
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>Posted to the registering window when a registered hotkey is pressed.</summary>
    public const int WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// Suppresses auto-repeat while the key is held. Without it, holding a Stream Deck button
    /// would enqueue a flood of trading commands.
    /// </summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED - another process owns this combination.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <param name="hWnd">Window that receives <see cref="WmHotkey"/>. Must belong to the calling thread.</param>
    /// <param name="id">Window-scoped identifier, returned in the message's wParam.</param>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    /// <remarks>Must be called from the same thread that registered the hotkey.</remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);
}
