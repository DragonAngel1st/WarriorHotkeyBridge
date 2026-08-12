namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// The hidden window that owns the hotkey registrations and receives <c>WM_HOTKEY</c>.
/// </summary>
/// <remarks>
/// <para>
/// A plain hidden top-level window is used rather than a message-only (<c>HWND_MESSAGE</c>)
/// window. Message-only windows are documented for inter-process messaging, and hotkey
/// delivery to them is not a documented guarantee; a normal window with no visible style is
/// the well-trodden path and costs nothing.
/// </para>
/// <para>
/// The window handle belongs to the thread that constructs it, and Win32 requires that the
/// same thread performs registration and unregistration.
/// </para>
/// </remarks>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public HotkeyWindow() =>
        CreateHandle(new CreateParams
        {
            Caption = "WarriorHotkeyBridge.HotkeyWindow",

            // No WS_VISIBLE, zero size, no parent: the window exists solely as a message target.
            Style = 0,
            ExStyle = 0,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Parent = nint.Zero,
        });

    /// <summary>Raised on the UI thread with the hotkey id from the message's wParam.</summary>
    public event EventHandler<int>? HotkeyPressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            HotkeyPressed?.Invoke(this, (int)m.WParam);
            m.Result = nint.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Routes exceptions escaping the window procedure to the application's handler.
    /// </summary>
    /// <remarks>
    /// <see cref="NativeWindow"/> catches everything thrown from <see cref="WndProc"/> and
    /// hands it here, where the base implementation discards it - unlike
    /// <see cref="Control"/>, which forwards to <see cref="Application.OnThreadException"/>.
    /// Nothing on today's path can throw, but Phase 5 attaches the command queue to
    /// <c>HotkeyPressed</c>, and a failure inside the hotkey pipeline vanishing without trace
    /// is precisely the bug that would be hardest to diagnose.
    /// </remarks>
    protected override void OnThreadException(Exception e) => Application.OnThreadException(e);

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyHandle();
        }
    }
}
