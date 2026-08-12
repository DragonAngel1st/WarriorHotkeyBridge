using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Renders the status indicator icons at runtime.
/// </summary>
/// <remarks>
/// Drawing rather than shipping .ico assets keeps the status colours defined in one place
/// next to the state model, and produces an icon at the current DPI's small-icon size instead
/// of a fixed 16x16 bitmap that Windows would have to rescale.
/// </remarks>
internal static partial class TrayIconFactory
{
    public static Color ColorFor(BridgeStatus status) => status switch
    {
        BridgeStatus.Starting => Color.FromArgb(0x80, 0x80, 0x80),
        BridgeStatus.WaitingForChrome => Color.FromArgb(0x80, 0x80, 0x80),
        BridgeStatus.Degraded => Color.FromArgb(0xE0, 0xA8, 0x00),
        BridgeStatus.Ready => Color.FromArgb(0x2E, 0xA0, 0x43),
        BridgeStatus.Error => Color.FromArgb(0xD1, 0x34, 0x38),
        _ => Color.FromArgb(0x80, 0x80, 0x80),
    };

    /// <summary>
    /// Creates one owned <see cref="Icon"/> per status. The caller disposes them.
    /// </summary>
    public static Dictionary<BridgeStatus, Icon> CreateStatusIcons()
    {
        Dictionary<BridgeStatus, Icon> icons = [];

        foreach (BridgeStatus status in Enum.GetValues<BridgeStatus>())
        {
            icons[status] = Create(ColorFor(status));
        }

        return icons;
    }

    private static Icon Create(Color fillColor)
    {
        Size size = SystemInformation.SmallIconSize;

        using var bitmap = new Bitmap(size.Width, size.Height);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float inset = Math.Max(1f, size.Width / 10f);
            var bounds = new RectangleF(inset, inset, size.Width - (inset * 2f), size.Height - (inset * 2f));

            using var fill = new SolidBrush(fillColor);
            graphics.FillEllipse(fill, bounds);

            // A darker rim keeps the dot legible against both light and dark taskbars.
            using var rim = new Pen(Darken(fillColor, 0.55f), Math.Max(1f, size.Width / 16f));
            graphics.DrawEllipse(rim, bounds);
        }

        nint iconHandle = bitmap.GetHicon();

        try
        {
            // Icon.FromHandle does NOT take ownership of the HICON, so the wrapper must not
            // outlive the handle. Cloning produces an independent, self-owning Icon that is
            // safe to keep, and the original handle is destroyed below.
            using Icon unowned = Icon.FromHandle(iconHandle);
            return (Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static Color Darken(Color color, float factor) => Color.FromArgb(
        color.A,
        (int)(color.R * factor),
        (int)(color.G * factor),
        (int)(color.B * factor));

    /// <summary>Releases an HICON obtained from <see cref="Bitmap.GetHicon"/>.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
