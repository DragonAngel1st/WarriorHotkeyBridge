using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Builds the notification-area icons.
/// </summary>
/// <remarks>
/// <para>
/// Two layers, because they answer different questions. The artwork says whether the bridge is
/// switched on, which is the thing the operator glances for. A status dot in the corner says
/// whether something is wrong with a session that <em>is</em> on - a hotkey another application
/// has taken, a lost Chrome connection - which is how a real F12 conflict was spotted, and which
/// on/off artwork alone cannot express.
/// </para>
/// <para>
/// The artwork is loaded from <c>assets\icons</c> beside the executable, and every failure falls
/// back to the drawn dot. A tray application whose icon is its only presence must never fail to
/// produce one: a missing file would otherwise mean no icon, and therefore no menu, and therefore
/// no way to reach anything.
/// </para>
/// </remarks>
internal static partial class TrayIconFactory
{
    /// <summary>Where the shipped artwork lives, relative to the executable.</summary>
    private const string AssetFolder = "assets";

    public static Color ColorFor(BridgeStatus status) => status switch
    {
        BridgeStatus.Starting => Color.FromArgb(0x80, 0x80, 0x80),
        BridgeStatus.Parked => Color.FromArgb(0x80, 0x80, 0x80),
        BridgeStatus.WaitingForChrome => Color.FromArgb(0x80, 0x80, 0x80),
        BridgeStatus.Degraded => Color.FromArgb(0xE0, 0xA8, 0x00),
        BridgeStatus.Ready => Color.FromArgb(0x2E, 0xA0, 0x43),
        BridgeStatus.Error => Color.FromArgb(0xD1, 0x34, 0x38),
        _ => Color.FromArgb(0x80, 0x80, 0x80),
    };

    /// <summary>
    /// Whether a status is worth marking on an armed icon.
    /// </summary>
    /// <remarks>
    /// Ready gets no dot: a clean session should read as calm, and a permanent green dot is one
    /// the eye stops seeing - which would make the amber and red ones easier to miss too.
    /// </remarks>
    private static bool NeedsStatusDot(BridgeStatus status) =>
        status is BridgeStatus.Degraded or BridgeStatus.Error or BridgeStatus.WaitingForChrome;

    /// <summary>
    /// Creates one owned <see cref="Icon"/> per status. The caller disposes them.
    /// </summary>
    public static Dictionary<BridgeStatus, Icon> CreateStatusIcons()
    {
        using Bitmap? armed = TryLoadArtwork("capturing-on.png");
        using Bitmap? parked = TryLoadArtwork("capturing-off.png");

        Dictionary<BridgeStatus, Icon> icons = [];

        foreach (BridgeStatus status in Enum.GetValues<BridgeStatus>())
        {
            Bitmap? artwork = status is BridgeStatus.Parked ? parked : armed;

            icons[status] = artwork is null
                ? Create(ColorFor(status))
                : Compose(artwork, status);
        }

        return icons;
    }

    /// <summary>Loads one PNG from the asset folder, or null if it is not usable.</summary>
    private static Bitmap? TryLoadArtwork(string fileName)
    {
        try
        {
            string root = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
            string path = Path.Combine(root, AssetFolder, "icons", fileName);

            if (!File.Exists(path))
            {
                return null;
            }

            // Loaded through a copy so the file handle is not held for the life of the process.
            // Bitmap keeps the stream open, and an asset the operator cannot replace or delete
            // while the bridge runs is a small trap for no benefit.
            using var fromFile = new Bitmap(path);
            return new Bitmap(fromFile);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException)
        {
            // OutOfMemoryException is what GDI+ throws for a corrupt or unsupported image, not
            // an actual memory problem.
            return null;
        }
    }

    /// <summary>Scales the artwork to the tray size and stamps the status dot on it.</summary>
    private static Icon Compose(Bitmap artwork, BridgeStatus status)
    {
        Size size = SystemInformation.SmallIconSize;

        using var bitmap = new Bitmap(size.Width, size.Height);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            graphics.DrawImage(artwork, new Rectangle(0, 0, size.Width, size.Height));

            if (NeedsStatusDot(status))
            {
                // Bottom-right, over the artwork rather than beside it: the tray gives us a fixed
                // square and there is no room to place anything outside it.
                float diameter = Math.Max(5f, size.Width * 0.44f);
                var dot = new RectangleF(
                    size.Width - diameter - 0.5f,
                    size.Height - diameter - 0.5f,
                    diameter,
                    diameter);

                // A contrasting ring first, so the dot reads against whatever the artwork puts
                // underneath it as well as against the taskbar.
                using var ring = new SolidBrush(Color.FromArgb(0xF2, 0x14, 0x14, 0x14));
                graphics.FillEllipse(ring, dot);

                using var fill = new SolidBrush(ColorFor(status));
                graphics.FillEllipse(fill, RectangleF.Inflate(dot, -diameter * 0.18f, -diameter * 0.18f));
            }
        }

        return ToIcon(bitmap);
    }

    /// <summary>The drawn fallback: a plain status dot, used when the artwork cannot be loaded.</summary>
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

        return ToIcon(bitmap);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
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
