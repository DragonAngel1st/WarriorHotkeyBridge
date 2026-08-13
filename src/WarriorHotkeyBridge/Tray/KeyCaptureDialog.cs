using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Sets one key value, either by pressing it or by typing it.
/// </summary>
/// <remarks>
/// <para>
/// Opened by clicking the cell, so it is the only place either value is set. That makes capture
/// the default path without making typing unreachable - which matters because some chords cannot
/// be pressed at all: the OS swallows Win+L and Ctrl+Alt+Del, and a shortcut may need authoring
/// for a SIM binding the operator cannot currently produce.
/// </para>
/// <para>
/// It listens on opening and stops the moment the field is clicked, because the two are mutually
/// exclusive - while keys are being intercepted for capture they cannot also be typed into a text
/// box. Listen re-arms it.
/// </para>
/// <para>
/// The caller suppresses hotkey dispatch around this and forwards presses of keys the bridge
/// already holds. Both are required: F13-F24 stay registered while this is open, so an
/// unsuppressed press would fire its binding, and a registered chord never reaches a focused
/// window at all.
/// </para>
/// </remarks>
internal sealed class KeyCaptureDialog : Form
{
    private readonly bool _hotkeyMode;
    private readonly Label _prompt;
    private readonly TextBox _value;
    private readonly Label _explanation;
    private readonly Button _listen;
    private readonly Button _ok;

    private bool _listening = true;
    private bool _updatingValue;

    public KeyCaptureDialog(string? current, bool hotkeyMode = false)
    {
        _hotkeyMode = hotkeyMode;

        Text = hotkeyMode ? "Hotkey from the deck" : "Shortcut sent into Level 2";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 260);
        KeyPreview = true;

        _prompt = new Label { AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false };

        _value = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = current ?? string.Empty,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font(SystemFonts.DialogFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 14f, FontStyle.Bold),
        };

        _explanation = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false,
            ForeColor = SystemColors.GrayText,
        };

        _listen = new Button { Text = "Listen for a key", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };

        _listen.Click += (_, _) => StartListening();

        // Clicking into the field is the signal that the operator wants to type. Capture has to
        // stop for that to be possible at all, since it intercepts every key before the box sees it.
        _value.Enter += (_, _) => StopListening();
        _value.MouseDown += (_, _) => StopListening();
        _value.TextChanged += (_, _) => OnValueTyped();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
        };

        buttons.Controls.AddRange([cancel, _ok, _listen]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_prompt, 0, 0);
        layout.Controls.Add(_value, 0, 1);
        layout.Controls.Add(_explanation, 0, 2);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);

        // Not AcceptButton while listening: Enter is a key someone may legitimately want to record,
        // and making it dismiss the dialog would leave it the one shortcut that cannot be captured.
        CancelButton = cancel;

        StartListening();
        Revalidate();
    }

    /// <summary>The chosen value, valid only when the dialog returned OK.</summary>
    public string? CapturedExpression { get; private set; }

    // ------------------------------------------------------------------ listening

    private void StartListening()
    {
        _listening = true;
        _listen.Enabled = false;
        AcceptButton = null;

        _prompt.Text = _hotkeyMode
            ? "Press the Stream Deck key you want to use.\r\n\r\n"
                + "If nothing appears, another application is holding that key. You can also click "
                + "the box below and type it, for example F13 or Ctrl+Alt+F13."
            : "Press the shortcut Warrior SIM expects - the keys you would press if you were typing "
                + "into the SIM yourself.\r\n\r\n"
                + "You can also click the box below and type it, for example Shift+Digit1.";

        _value.BackColor = Color.FromArgb(240, 248, 255);

        // Focus anywhere but the text box, or entering it would immediately cancel listening.
        _listen.Focus();
    }

    private void StopListening()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        _listen.Enabled = true;
        AcceptButton = _ok;
        _value.BackColor = SystemColors.Window;

        _prompt.Text = _hotkeyMode
            ? "Type the key, for example F13 or Ctrl+Alt+F13.\r\n\r\n"
                + "Or choose Listen for a key and press it on the deck."
            : "Type the shortcut, for example Shift+Digit1 or Control+KeyQ.\r\n\r\n"
                + "Or choose Listen for a key and press it.";
    }

    // ------------------------------------------------------------------ input

    /// <summary>
    /// Intercepts keys before the dialog manager, while listening.
    /// </summary>
    /// <remarks>
    /// KeyDown is not enough: Tab, Enter, Escape and the arrows are consumed as navigation before
    /// any KeyDown handler runs, so a capture built on it silently cannot record precisely the
    /// keys a trading shortcut is most likely to use.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_listening)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Escape with no modifiers closes. Shift+Escape and friends remain capturable, so the key
        // is not entirely out of reach - it just cannot be recorded on its own.
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

        if (_hotkeyMode)
        {
            SetValue(DescribeAsGestureText(keyData));
        }
        else if (WindowsKeyTranslator.TryTranslate(keyData, out string? expression, out string? error))
        {
            SetValue(expression);
        }
        else
        {
            ShowProblem(error);
        }

        return true;
    }

    /// <summary>
    /// Accepts a press the bridge intercepted, which never reaches a focused window.
    /// </summary>
    /// <remarks>
    /// Windows delivers a registered hotkey to the registering window. Without this path the
    /// dialog would be blind to every key already configured - including, in Sends mode, the case
    /// where the operator wants the deck key and the SIM shortcut to be the same chord.
    /// </remarks>
    public void AcceptCapturedGesture(HotkeyGesture gesture)
    {
        if (!_listening)
        {
            return;
        }

        if (_hotkeyMode)
        {
            SetValue(gesture.Display);
            return;
        }

        bool meta = gesture.Modifiers.HasFlag(HotkeyModifiers.Windows);

        if (WindowsKeyTranslator.TryTranslate(gesture.ToWindowsKeyData(), meta, out string? sends, out string? error))
        {
            SetValue(sends);
        }
        else
        {
            ShowProblem(error);
        }
    }

    private void SetValue(string text)
    {
        _updatingValue = true;
        _value.Text = text;
        _updatingValue = false;

        Revalidate();
    }

    private void OnValueTyped()
    {
        if (!_updatingValue)
        {
            Revalidate();
        }
    }

    // ------------------------------------------------------------------ validation

    /// <summary>
    /// Validates with the same parsers the configuration uses, so a value accepted here can never
    /// be one the bridge then refuses.
    /// </summary>
    private void Revalidate()
    {
        string text = _value.Text.Trim();

        if (text.Length == 0)
        {
            CapturedExpression = null;
            _explanation.Text = string.Empty;
            _ok.Enabled = false;
            return;
        }

        if (_hotkeyMode)
        {
            if (!HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out string? gestureError))
            {
                ShowProblem(gestureError);
                return;
            }

            CapturedExpression = text;
            _ok.Enabled = true;

            string? risk = gesture.DescribeGlobalCaptureRisk();
            _explanation.Text = risk ?? "Ready to use.";
            _explanation.ForeColor = risk is null ? SystemColors.GrayText : Color.FromArgb(150, 90, 0);
            return;
        }

        if (!PlaywrightKeys.TryNormalize(text, out string? normalized, out string? error))
        {
            ShowProblem(error);
            return;
        }

        CapturedExpression = normalized;
        _ok.Enabled = true;

        string? ambiguity = PlaywrightKeys.DescribeAmbiguity(normalized);
        _explanation.Text = ambiguity ?? WindowsKeyTranslator.Describe(normalized);
        _explanation.ForeColor = ambiguity is null ? SystemColors.GrayText : Color.FromArgb(150, 90, 0);
    }

    private void ShowProblem(string? error)
    {
        CapturedExpression = null;
        _explanation.Text = error;
        _explanation.ForeColor = Color.FromArgb(150, 35, 35);
        _ok.Enabled = false;
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

        _explanation.Text = $"Holding {string.Join('+', held)} - now press the key.";
        _explanation.ForeColor = SystemColors.GrayText;
    }

    /// <summary>
    /// Renders a WinForms key as the text the gesture parser reads.
    /// </summary>
    /// <remarks>
    /// Goes through text rather than building a gesture directly, so a captured value and a typed
    /// one travel the same parsing path - including its rejection of modifier-only chords and of
    /// keys that cannot be registered. Two routes into one field that disagreed about validity
    /// would be worse than one route.
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();

        // Re-armed here, not only in the constructor. WinForms gives focus to the first control in
        // the tab order when the form is shown, which is the text box - and entering the text box
        // is precisely the signal that stops listening. Setting focus before the form exists does
        // not survive that, so the dialog opened in typing mode however it was configured.
        StartListening();
    }
}
