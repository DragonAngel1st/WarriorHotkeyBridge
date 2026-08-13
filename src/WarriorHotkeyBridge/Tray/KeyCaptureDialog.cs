using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Records the shortcut the operator presses, and reports it as a Playwright key expression.
/// </summary>
/// <remarks>
/// <para>
/// Modal on purpose. Capture cannot be an always-on property of the cell, because while it is
/// active the operator cannot type, paste or correct - and a mode you can be in without noticing
/// is the wrong mode for a field that decides which order a key places. A dialog makes the mode
/// visible, bounds it, and gives Escape somewhere obvious to go.
/// </para>
/// <para>
/// The caller is responsible for suppressing hotkey dispatch around this. F13-F24 remain
/// registered globally while it is open, so without that a press intended for capture fires its
/// binding instead.
/// </para>
/// </remarks>
internal sealed class KeyCaptureDialog : Form
{
    private readonly Label _prompt;
    private readonly Label _captured;
    private readonly Label _explanation;
    private readonly Button _ok;

    private readonly bool _hotkeyMode;

    public KeyCaptureDialog(string? current, bool hotkeyMode = false)
    {
        _hotkeyMode = hotkeyMode;
        Text = hotkeyMode ? "Capture hotkey" : "Capture shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 240);
        KeyPreview = true;

        _prompt = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            UseMnemonic = false,
            Text = hotkeyMode
                ? "Press the Stream Deck key you want to use.\r\n\r\n"
                    + "If nothing appears, another application is holding that key - close it and "
                    + "try again. Escape closes this without changing anything."
                : "Press the shortcut that Warrior SIM expects - the keys you would press if you "
                    + "were typing into the SIM yourself.\r\n\r\n"
                    + "Escape closes this without changing anything.",
        };

        _captured = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false,
            Font = new Font(SystemFonts.DialogFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 15f, FontStyle.Bold),
            Text = string.IsNullOrWhiteSpace(current) ? "waiting..." : current,
            ForeColor = string.IsNullOrWhiteSpace(current) ? SystemColors.GrayText : SystemColors.ControlText,
        };

        _explanation = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false,
            ForeColor = SystemColors.GrayText,
        };

        _ok = new Button { Text = "Use this", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(12, 4, 12, 4), Enabled = false };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
        };

        buttons.Controls.AddRange([cancel, _ok]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_prompt, 0, 0);
        layout.Controls.Add(_captured, 0, 1);
        layout.Controls.Add(_explanation, 0, 2);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);

        // Not AcceptButton: Enter is a key someone may legitimately want to capture, and making it
        // dismiss the dialog would make it the one shortcut that cannot be recorded.
        CancelButton = cancel;
    }

    /// <summary>The captured expression, valid only when the dialog returned OK.</summary>
    public string? CapturedExpression { get; private set; }

    /// <summary>
    /// Intercepts keys before the dialog manager does.
    /// </summary>
    /// <remarks>
    /// KeyDown is not enough. Tab, the arrows, Enter and Escape are consumed by WinForms as
    /// navigation before any KeyDown handler runs, so a capture built on KeyDown silently cannot
    /// record precisely the keys a trading shortcut is most likely to use.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Escape with no modifiers cancels. Shift+Escape and friends are still capturable, so the
        // key itself is not entirely out of reach - it just cannot be recorded on its own.
        if (keyData is Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }

        if (WindowsKeyTranslator.IsModifierOnly(keyData))
        {
            ShowPending(keyData);
            return true;
        }

        // The Hotkey column names a Windows key, not a browser one, so it is parsed rather than
        // translated. This path serves keys the bridge does NOT currently hold; the ones it does
        // arrive through AcceptCapturedGesture instead, because Windows never delivers a
        // registered hotkey to the focused window.
        if (_hotkeyMode)
        {
            if (HotkeyGesture.TryParse(DescribeAsGestureText(keyData), out HotkeyGesture gesture, out string? gestureError))
            {
                AcceptCapturedGesture(gesture);
            }
            else
            {
                ShowRejection(gestureError);
            }

            return true;
        }

        if (WindowsKeyTranslator.TryTranslate(keyData, out string? expression, out string? error))
        {
            CapturedExpression = expression;
            _captured.Text = expression;
            _captured.ForeColor = SystemColors.ControlText;
            _explanation.Text = WindowsKeyTranslator.Describe(expression);
            _explanation.ForeColor = SystemColors.GrayText;
            _ok.Enabled = true;
        }
        else
        {
            CapturedExpression = null;
            _captured.Text = "cannot be sent";
            _captured.ForeColor = Color.FromArgb(150, 35, 35);
            _explanation.Text = error;
            _explanation.ForeColor = Color.FromArgb(150, 35, 35);
            _ok.Enabled = false;
        }

        return true;
    }

    /// <summary>
    /// Accepts a gesture that arrived as WM_HOTKEY rather than as keyboard input.
    /// </summary>
    /// <remarks>
    /// Called by the editor when the bridge forwards a press of a key it already holds. Windows
    /// delivers a registered hotkey to the registering window, never to the focused one, so
    /// without this path the dialog would be blind to precisely the F13-F24 keys it exists to
    /// record.
    /// </remarks>
    public void AcceptCapturedGesture(HotkeyGesture gesture)
    {
        string text = gesture.Display;

        CapturedExpression = text;
        _captured.Text = text;
        _captured.ForeColor = SystemColors.ControlText;
        _explanation.Text = gesture.DescribeGlobalCaptureRisk() is { } risk
            ? risk
            : "Ready to use.";

        _explanation.ForeColor = gesture.DescribeGlobalCaptureRisk() is null
            ? SystemColors.GrayText
            : Color.FromArgb(150, 90, 0);

        _ok.Enabled = true;
    }

    private void ShowRejection(string? error)
    {
        CapturedExpression = null;
        _captured.Text = "cannot be used";
        _captured.ForeColor = Color.FromArgb(150, 35, 35);
        _explanation.Text = error;
        _explanation.ForeColor = Color.FromArgb(150, 35, 35);
        _ok.Enabled = false;
    }

    /// <summary>
    /// Renders a WinForms key as the text the gesture parser reads.
    /// </summary>
    /// <remarks>
    /// Goes through text rather than constructing a gesture directly so capture and a typed value
    /// travel the same parsing path - including its rejection of modifier-only chords and of keys
    /// that cannot be registered. Two routes into the same field that disagreed about what is
    /// valid would be worse than one route.
    /// </remarks>
    private static string DescribeAsGestureText(Keys keyData)
    {
        List<string> parts = [];

        if (keyData.HasFlag(Keys.Control))
        {
            parts.Add("Ctrl");
        }

        if (keyData.HasFlag(Keys.Alt))
        {
            parts.Add("Alt");
        }

        if (keyData.HasFlag(Keys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add((keyData & Keys.KeyCode).ToString());
        return string.Join('+', parts);
    }

    /// <summary>Shows the modifiers held so far, so the dialog feels responsive mid-chord.</summary>
    private void ShowPending(Keys keyData)
    {
        List<string> held = [];

        if (keyData.HasFlag(Keys.Control))
        {
            held.Add("Control");
        }

        if (keyData.HasFlag(Keys.Alt))
        {
            held.Add("Alt");
        }

        if (keyData.HasFlag(Keys.Shift))
        {
            held.Add("Shift");
        }

        if (held.Count == 0)
        {
            return;
        }

        _captured.Text = string.Join('+', held) + "+...";
        _captured.ForeColor = SystemColors.GrayText;
        _explanation.Text = $"Now press the key to go with {(held.Count == 1 ? "it" : "them")}.";
        _explanation.ForeColor = SystemColors.GrayText;
        _ok.Enabled = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    /// <summary>Named for the log, so a capture session is identifiable afterwards.</summary>
    public static string DescribeForLog(string? expression) =>
        expression is null ? "(cancelled)" : $"{expression} ({WindowsKeyTranslator.Describe(expression)})";
}
