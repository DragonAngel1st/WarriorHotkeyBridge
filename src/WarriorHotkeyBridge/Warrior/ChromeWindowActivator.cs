using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Warrior;

internal interface IPageActivator
{
    /// <summary>
    /// Makes the page the active tab and raises its Chrome window above other applications.
    /// </summary>
    /// <returns>True if the Chrome window was raised; false if only the tab could be activated.</returns>
    Task<bool> ActivateAsync(IPage page, CancellationToken cancellationToken);

    /// <summary>Drops the cached window handle, e.g. after the target page changes.</summary>
    void Invalidate();
}

/// <summary>
/// Brings the SIM tab and its Chrome window to the front before a chord is dispatched.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms are required, and measurement is the reason. CDP's
/// <c>Target.activateTarget</c> (what <c>BringToFrontAsync</c> calls) selects the tab inside
/// its window but leaves the OS foreground alone - verified against a real session, where the
/// foreground window was unchanged before and after. Raising the window therefore needs Win32.
/// </para>
/// <para>
/// Windows only grants <c>SetForegroundWindow</c> to a process that received the last input
/// event. This process receives the hotkey itself through <c>RegisterHotKey</c>, which is what
/// makes the call legitimate rather than a focus-stealing hack.
/// </para>
/// </remarks>
internal sealed partial class ChromeWindowActivator : IPageActivator
{
    /// <summary>Chrome's top-level window class.</summary>
    private const string ChromeWindowClass = "Chrome_WidgetWin_1";

    private const int SwRestore = 9;

    /// <summary>Total pixels of mismatch tolerated when matching CDP bounds to a window rect.</summary>
    private const int BoundsMatchTolerance = 40;

    private readonly ILogger<ChromeWindowActivator> _logger;

    // Resolving the window costs a CDP round trip and a window enumeration, so the answer is
    // cached: the operator's window layout changes far less often than they press a key.
    /// <summary>How long a resolved window handle may be reused before it is re-derived.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private IPage? _cachedPage;
    private nint _cachedWindow;
    private long _cachedAt;

    public ChromeWindowActivator(ILogger<ChromeWindowActivator> logger) => _logger = logger;

    public void Invalidate()
    {
        _cachedPage = null;
        _cachedWindow = 0;
    }

    public async Task<bool> ActivateAsync(IPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Step 1: make it the active tab within its own window.
        await page.BringToFrontAsync().ConfigureAwait(false);

        // Step 2: raise that window above everything else.
        nint window = await ResolveWindowAsync(page, cancellationToken).ConfigureAwait(false);

        if (window == 0)
        {
            _logger.ChromeWindowNotResolved();
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        if (SetForegroundWindow(window))
        {
            return true;
        }

        _logger.ChromeWindowRaiseFailed(Marshal.GetLastWin32Error());
        return false;
    }

    private async Task<nint> ResolveWindowAsync(IPage page, CancellationToken cancellationToken)
    {
        // Time-limited as well as page-keyed. A tab can be dragged from one Chrome window to
        // another without the IPage changing at all, which would leave the cached handle
        // pointing at the window the tab used to live in - raising the wrong one on every press.
        // Re-resolving costs a CDP round trip, so it is bounded rather than done every time.
        bool expired = Stopwatch.GetElapsedTime(_cachedAt) > CacheLifetime;

        if (!expired && ReferenceEquals(_cachedPage, page) && _cachedWindow != 0 && IsWindow(_cachedWindow))
        {
            return _cachedWindow;
        }

        nint resolved = await ResolveByBoundsAsync(page, cancellationToken).ConfigureAwait(false);

        // Falling back to the title is deliberately second: two SIM windows can share a title,
        // so it is only trustworthy when it produces exactly one match.
        resolved = resolved != 0 ? resolved : await ResolveByTitleAsync(page).ConfigureAwait(false);

        if (resolved != 0)
        {
            _cachedPage = page;
            _cachedWindow = resolved;
            _cachedAt = Stopwatch.GetTimestamp();
        }

        return resolved;
    }

    /// <summary>
    /// Matches the window Chrome reports for this tab against the enumerated top-level windows.
    /// </summary>
    private async Task<nint> ResolveByBoundsAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            ICDPSession session = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);

            try
            {
                JsonElement? response = await session
                    .SendAsync("Browser.getWindowForTarget")
                    .ConfigureAwait(false);

                if (response is null || !response.Value.TryGetProperty("bounds", out JsonElement bounds))
                {
                    return 0;
                }

                var target = new Rect(
                    bounds.GetProperty("left").GetInt32(),
                    bounds.GetProperty("top").GetInt32(),
                    bounds.GetProperty("width").GetInt32(),
                    bounds.GetProperty("height").GetInt32());

                cancellationToken.ThrowIfCancellationRequested();

                return FindClosestChromeWindow(target);
            }
            finally
            {
                await session.DetachAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or KeyNotFoundException or InvalidOperationException)
        {
            // Older Chrome builds, or a target that has gone away. The title fallback still applies.
            _logger.ChromeWindowBoundsUnavailable(ex.Message);
            return 0;
        }
    }

    private static async Task<nint> ResolveByTitleAsync(IPage page)
    {
        string title;

        try
        {
            title = await page.TitleAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        List<nint> matches = [];

        foreach (nint window in EnumerateChromeWindows())
        {
            if (GetWindowTitle(window).StartsWith(title, StringComparison.Ordinal))
            {
                matches.Add(window);
            }
        }

        // Exactly one, or nothing: an ambiguous title must not pick a window at random when
        // that window is about to receive a trading keystroke.
        return matches.Count == 1 ? matches[0] : 0;
    }

    /// <summary>
    /// Matches CDP window bounds against the enumerated windows, correcting for display scaling.
    /// </summary>
    /// <remarks>
    /// Chrome reports bounds in device-independent pixels. This process declares per-monitor
    /// DPI awareness, so <c>GetWindowRect</c> returns physical pixels. On any display scaled
    /// above 100% the two disagree by the scale factor, which is enough to miss every window.
    /// Each candidate is therefore compared at its own monitor's scale - which also handles a
    /// mixed-DPI setup, where a single global factor would be wrong for one of the screens.
    /// </remarks>
    private nint FindClosestChromeWindow(Rect target)
    {
        List<(nint Window, double SizeMismatch, double OriginMismatch)> candidates = [];

        foreach (nint window in EnumerateChromeWindows())
        {
            if (!GetWindowRect(window, out Rect rect))
            {
                continue;
            }

            uint dpi = GetDpiForWindow(window);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;

            // Size and origin are scored separately and deliberately. Width and height convert
            // correctly with the window's own monitor scale, but the ORIGIN does not: Chrome's
            // DIP coordinate space places each monitor at an offset that is not simply the
            // physical offset divided by that monitor's scale. On a mixed-DPI setup a single
            // multiply puts the origin hundreds of pixels out while the size still matches, so
            // scoring them together would reject the correct window.
            double sizeMismatch = Math.Abs(rect.Width - (target.Width * scale))
                + Math.Abs(rect.Height - (target.Height * scale));

            double originMismatch = Math.Abs(rect.Left - (target.Left * scale))
                + Math.Abs(rect.Top - (target.Top * scale));

            _logger.ChromeWindowCandidate(
                window, rect.Left, rect.Top, rect.Width, rect.Height, scale, sizeMismatch, originMismatch);

            candidates.Add((window, sizeMismatch, originMismatch));
        }

        double sizeTolerance = BoundsMatchTolerance + ((Math.Abs(target.Width) + Math.Abs(target.Height)) * 0.02);

        List<(nint Window, double SizeMismatch, double OriginMismatch)> bySize =
            [.. candidates.Where(c => c.SizeMismatch <= sizeTolerance).OrderBy(c => c.OriginMismatch)];

        if (bySize.Count == 0)
        {
            return 0;
        }

        if (bySize.Count == 1)
        {
            return bySize[0].Window;
        }

        // Several windows are the same size, so the origin has to break the tie — and it must do
        // so decisively. Raising the wrong Chrome window puts a trading chord in front of the
        // wrong session, so an unclear answer fails closed rather than picking the nearest.
        bool decisive = bySize[0].OriginMismatch <= sizeTolerance
            && bySize[1].OriginMismatch > bySize[0].OriginMismatch * 2;

        if (decisive)
        {
            return bySize[0].Window;
        }

        _logger.ChromeWindowAmbiguous(bySize.Count);
        return 0;
    }

    private static List<nint> EnumerateChromeWindows()
    {
        List<nint> windows = [];

        EnumWindows(
            (window, _) =>
            {
                if (IsWindowVisible(window) && GetClassNameOf(window) == ChromeWindowClass)
                {
                    windows.Add(window);
                }

                return true;
            },
            nint.Zero);

        return windows;
    }

    private static string GetClassNameOf(nint window)
    {
        Span<char> buffer = stackalloc char[64];
        int length = GetClassName(window, buffer, buffer.Length);
        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    private static string GetWindowTitle(nint window)
    {
        int length = GetWindowTextLength(window);

        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = new char[length + 1];
        int written = GetWindowText(window, buffer, buffer.Length);
        return written <= 0 ? string.Empty : new string(buffer[..written]);
    }

    /// <summary>Screen rectangle, normalised to position plus size.</summary>
    private readonly record struct Rect(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool GetWindowRect(nint window, out Rect rect)
    {
        if (!GetWindowRect(window, out NativeRect native))
        {
            rect = default;
            return false;
        }

        rect = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        return true;
    }

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    // DllImport rather than LibraryImport: the source generator does not marshal delegate
    // callbacks, and a function-pointer rewrite would obscure a small, well-understood call.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint window, Span<char> text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint window, Span<char> className, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);

    /// <summary>Per-monitor DPI of the window's display. 96 means 100% scaling.</summary>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
