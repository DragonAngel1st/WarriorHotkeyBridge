using System.ComponentModel;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// The hotkey mapping editor.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than with a designer so the whole layout is reviewable in one file and
/// carries its reasoning with it. It is a grid because that is what the data is: a short list of
/// rows, each a key and what it sends.
/// </para>
/// <para>
/// Keys are set by clicking the cell, which opens a capture dialog that accepts either a press or
/// typing. The press half is the part with teeth: the bridge holds F13-F24 globally the entire
/// time this dialog is open, so capture only works because dispatch is suspended and intercepted
/// chords are forwarded into the dialog. See <see cref="CaptureInto"/>.
/// </para>
/// </remarks>
internal sealed class HotkeyEditorForm : Form
{
    /// <summary>
    /// How long the targeting test is given before the dialog stops waiting on it.
    /// </summary>
    /// <remarks>
    /// Far longer than a test takes - a live one measures in tens of milliseconds - because this
    /// is not a latency budget. It exists so the button cannot be left saying "Testing..."
    /// forever if the command consumer stops before reaching the request, which is what happens
    /// when the bridge shuts down with the editor still open.
    /// </remarks>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hover text for the two key columns.
    /// </summary>
    /// <remarks>
    /// Long, and deliberately so. Both columns are read-only precisely so that clicking them can
    /// open capture instead of swallowing keystrokes in place - but a read-only cell looks like
    /// one that cannot be changed at all, so without this the gesture is unguessable.
    /// </remarks>
    private const string HotkeyColumnHint =
        "Click inside this cell, then press the key you want to record - or type it.\r\n"
        + "This is the key the bridge listens for while you are working somewhere else: F13 to "
        + "F24, on their own or with Ctrl, Alt, Shift or Win. It is what your Stream Deck sends.";

    private const string SendColumnHint =
        "Click inside this cell, then press the key sequence you want recorded - or type it.\r\n"
        + "This is what gets delivered into the Level 2 & Order Entry panel when the hotkey is "
        + "pressed. What it then DOES is set in Warrior SIM's own hotkey settings, not here.";

    private readonly IHotkeyPresetProvider _presets;
    private readonly BindingList<BindingRow> _rows = [];
    private readonly DataGridView _grid;
    private readonly TextBox _problems;
    private readonly ComboBox _presetPicker;
    private readonly Button _save;
    private readonly Label _summary;
    private Label _help = null!;
    private Label _testResult = null!;
    private Button _saveAsPreset = null!;
    private Button _test = null!;
    private readonly ToolTip _tooltip = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };
    private readonly Func<Action<HotkeyGesture>, IDisposable>? _captureRegisteredPresses;
    private readonly Func<Task<CommandResult>>? _runTargetingTest;

    private bool _suppressValidation;

    public HotkeyEditorForm(
        IReadOnlyDictionary<string, HotkeyBindingConfig> current,
        IHotkeyPresetProvider presets,
        Func<Action<HotkeyGesture>, IDisposable>? captureRegisteredPresses = null,
        Func<Task<CommandResult>>? runTargetingTest = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _presets = presets;

        // Optional so the form stays constructible in tests without a hotkey service. In the
        // application it is always supplied; the capture handlers refuse to run without it rather
        // than capturing while the keys are live.
        _captureRegisteredPresses = captureRegisteredPresses;

        // Likewise optional, and likewise always supplied in the application. Without it the Test
        // button is disabled rather than absent, so the dialog does not change shape depending on
        // how it was constructed.
        _runTargetingTest = runTargetingTest;

        Text = $"{AppInfo.ProductName} - Hotkeys";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 480);
        Size = new Size(1040, 600);
        ShowInTaskbar = true;
        MinimizeBox = false;

        _grid = BuildGrid();
        _problems = BuildProblemBox();
        _presetPicker = BuildPresetPicker();
        _summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        _save = new Button { Text = "Save && Apply", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

        Controls.Add(BuildLayout());

        LoadRows(current);
        Revalidate();
    }

    /// <summary>The edited set, valid and ready to persist. Only meaningful after DialogResult.OK.</summary>
    public IReadOnlyDictionary<string, HotkeyBindingConfig> Result { get; private set; } =
        new Dictionary<string, HotkeyBindingConfig>(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ layout

    private TableLayoutPanel BuildLayout()
    {
        // UseMnemonic off because the text contains "Level 2 & Order Entry", and a Label treats
        // '&' as the underline-the-next-letter prefix - it renders as "Level 2  Order Entry" and
        // eats the ampersand entirely.
        _help = new Label
        {
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
            Text =
                "Each row sends a keyboard shortcut into the Level 2 & Order Entry panel. What that "
                + "shortcut DOES is set in Warrior SIM's own hotkey settings - this only delivers it.\r\n"
                + "Click a Hotkey or Sends cell to set it - press the key, or type it. "
                + "Right-click a row to add, duplicate or remove it.",
        };

        // Hidden until a test has run. A permanently visible empty line between the toolbar and
        // the grid would read as a field waiting to be filled in.
        _testResult = new Label
        {
            AutoSize = true,
            UseMnemonic = false,
            Visible = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        var loadPreset = new Button { Text = "Load", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        loadPreset.Click += OnLoadPreset;

        _saveAsPreset = new Button { Text = "Copy preset...", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        _saveAsPreset.Click += OnSaveAsPreset;

        // The name has to be short enough to sit in a toolbar; the tooltip carries what it
        // actually does, which is too long to be a label.
        _tooltip.SetToolTip(
            _saveAsPreset,
            "Copy the hotkeys currently shown here into a new preset file, under a name you choose.\r\n"
            + "This does not change your active hotkeys - use Save & Apply for that.");

        // Replaces the Action column's Test entry. Everyone needs to answer "is it aimed at the
        // right place?", and making that a binding meant spending one of twelve deck keys on it
        // and understanding a column that exists for nothing else.
        _test = new Button
        {
            Text = "Test targeting",
            AutoSize = true,
            Margin = new Padding(24, 0, 0, 0),
            Enabled = _runTargetingTest is not null,
        };

        _test.Click += OnTestTargeting;

        _tooltip.SetToolTip(
            _test,
            "Runs everything a trading key does except the keystroke: finds the Warrior SIM page,\r\n"
            + "selects its Level 2 & Order Entry panel, and brings the Chrome window to the front.\r\n"
            + "Nothing is sent, so this is always safe to press.");

        // Wrapping ON here, unlike the OK/Cancel panel. That one had to stay on one line because a
        // stacked Save-above-Cancel looks broken; a toolbar spilling onto a second row is ordinary,
        // and the alternative is what happened when a sixth button was added - Remove row simply
        // vanished past the right edge with nothing to indicate it existed.
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8),
        };

        toolbar.Controls.AddRange(
        [
            new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 6, 0, 0) },
            _presetPicker,
            loadPreset,
            _saveAsPreset,
            _test,
        ]);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        _save.Click += OnSave;

        // WrapContents off: with it on, the panel wrapped three buttons into a vertical stack the
        // moment its width was even slightly tight.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        buttons.Controls.AddRange([cancel, _save]);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_summary, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        // A table rather than nested docking. Docking resolves in reverse z-order, which is easy
        // to get subtly wrong and impossible to read later; rows with explicit styles say what
        // grows and what does not.
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 6,
        };

        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // help
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // toolbar
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // test result
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // grid
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // problems
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons

        host.Controls.Add(_help, 0, 0);
        host.Controls.Add(toolbar, 0, 1);
        host.Controls.Add(_testResult, 0, 2);
        host.Controls.Add(_grid, 0, 3);
        host.Controls.Add(_problems, 0, 4);
        host.Controls.Add(bottom, 0, 5);

        // An AutoSize label only wraps if something bounds its width. Without this it lays out as
        // one enormously wide line and the form simply clips it, which is what the fixed height
        // was hiding.
        host.Resize += (_, _) => ConstrainLabelWidths(host);
        ConstrainLabelWidths(host);

        CancelButton = cancel;
        return host;
    }

    /// <remarks>
    /// Both wrapping labels, not just the help text. The test result carries a failure reason
    /// straight from the executor, which is the longer of the two and the one it matters most to
    /// be able to read.
    /// </remarks>
    private void ConstrainLabelWidths(TableLayoutPanel host)
    {
        var bound = new Size(Math.Max(120, host.ClientSize.Width - host.Padding.Horizontal), 0);

        _help.MaximumSize = bound;
        _testResult.MaximumSize = bound;
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,

            // Off, with an explicit Add row button instead. The built-in placeholder renders as a
            // permanent blank row under the real ones and reads as a stray record rather than as
            // an invitation - especially in a grid whose rows are trading keys.
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,

            // Otherwise the space below the last row is control-grey and looks like a dead panel
            // pasted under the table.
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            DataSource = _rows,
        };

        // Level2Index is deliberately not a column. It selects a Level 2 panel by position in the
        // page, which is the fragile mechanism colour-link is meant to replace - so putting it in
        // front of every operator would be advertising the thing we intend to retire. Values
        // already in the configuration file are preserved untouched; the row carries them through.
        grid.Columns.AddRange(
            TextColumn(nameof(BindingRow.Hotkey), "Hotkey (from the deck)", 150),
            TextColumn(nameof(BindingRow.Send), "Sends into Level 2", 180),
            Choice(nameof(BindingRow.Action), "Action", 120, ["", "Test", "Diagnostics"]),
            TextColumn(nameof(BindingRow.Label), "Label (yours - never interpreted)", 300));

        grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        // Hidden rather than removed, and hidden rather than left in place. Both named actions
        // are reachable without it now - Test is the button above, Diagnostics is a tray menu
        // item - so the column was a step every operator had to understand in order to leave it
        // alone. Dropping the column outright would instead drop the value on save, silently
        // disarming the Test and Diagnostics keys of anyone who already has them configured.
        grid.Columns[2].Visible = false;

        // The two key columns are set through the capture dialog, never typed in place. Editing
        // them here would mean intercepting keystrokes inside a grid cell, and a cell that
        // swallows every key while looking like an ordinary text box is a worse thing to hand
        // someone than a dialog that says what it is doing. The dialog still allows typing, so
        // nothing is lost - only moved somewhere it can be explained.
        grid.Columns[0].ReadOnly = true;
        grid.Columns[1].ReadOnly = true;
        grid.Columns[0].DefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        grid.Columns[1].DefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);

        grid.Columns[0].ToolTipText = HotkeyColumnHint;
        grid.Columns[1].ToolTipText = SendColumnHint;

        // Per cell as well as per header, because the header is not where anyone looks when they
        // are trying to work out why a cell will not accept typing. The event fires only for a
        // data-bound or virtual grid; this one is data-bound.
        grid.CellToolTipTextNeeded += OnCellToolTipTextNeeded;

        // A row whose only payload is a now-hidden Action would show an empty Sends cell and read
        // as half-finished. Display only - nothing is written back.
        grid.CellFormatting += OnFormatCell;

        grid.CellClick += OnCellClick;
        grid.CellDoubleClick += OnCellClick;

        // Row management without toolbar buttons: right-click for the menu, Insert and Delete for
        // the keyboard. Both are standard Windows list idioms, and the menu is what makes them
        // discoverable - a keyboard-only affordance nobody is told about is not an affordance.
        grid.ContextMenuStrip = BuildRowMenu();
        grid.KeyDown += OnGridKeyDown;

        // Validation on every edit rather than only on save, so the operator sees the problem
        // beside the row that has it instead of a list at the end.
        grid.CellValueChanged += (_, _) => Revalidate();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.UserDeletedRow += (_, _) => Revalidate();
        grid.DataError += (_, e) => e.ThrowException = false;

        return grid;
    }

    private ContextMenuStrip BuildRowMenu()
    {
        var add = new ToolStripMenuItem("Add row", null, (_, _) => AddRow()) { ShortcutKeyDisplayString = "Insert" };
        var duplicate = new ToolStripMenuItem("Duplicate row", null, (_, _) => DuplicateRow());
        var remove = new ToolStripMenuItem("Remove row", null, (_, _) => RemoveRow()) { ShortcutKeyDisplayString = "Delete" };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([add, duplicate, new ToolStripSeparator(), remove]);

        // Duplicate and Remove are meaningless without a row, and a menu offering actions that
        // silently do nothing teaches people to distrust it.
        menu.Opening += (_, _) =>
        {
            bool hasRow = _grid.CurrentRow is { Index: >= 0 and var i } && i < _rows.Count;
            duplicate.Enabled = hasRow;
            remove.Enabled = hasRow;
        };

        return menu;
    }

    /// <remarks>
    /// Delete is handled rather than left to the grid's own row deletion, so removing a row goes
    /// through the same path as the menu and revalidates afterwards.
    /// </remarks>
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Insert)
        {
            AddRow();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Delete && _grid.CurrentRow is { Index: >= 0 })
        {
            RemoveRow();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the capture dialog for whichever key column was clicked.
    /// </summary>
    /// <remarks>
    /// Both single and double click, because the columns are read-only: a double click on a
    /// read-only cell produces no edit, so without this the second click would appear to do
    /// nothing at all.
    /// </remarks>
    private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        if (e.ColumnIndex == 0)
        {
            CaptureInto(e.RowIndex, hotkey: true);
        }
        else if (e.ColumnIndex == 1)
        {
            CaptureInto(e.RowIndex, hotkey: false);
        }
    }

    private static void OnCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e) =>
        e.ToolTipText = e.ColumnIndex switch
        {
            0 => HotkeyColumnHint,
            1 => SendColumnHint,
            _ => e.ToolTipText,
        };

    /// <summary>
    /// Says what a row with no Send is for, now that the Action column is hidden.
    /// </summary>
    private void OnFormatCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        BindingRow row = _rows[e.RowIndex];

        if (!string.IsNullOrWhiteSpace(row.Send) || string.IsNullOrWhiteSpace(row.Action))
        {
            return;
        }

        e.Value = $"({row.Action.Trim()} - sends nothing)";
        e.CellStyle.ForeColor = Color.FromArgb(110, 110, 110);
        e.FormattingApplied = true;
    }

    private static DataGridViewTextBoxColumn TextColumn(string property, string header, int width) =>
        new() { DataPropertyName = property, HeaderText = header, Width = width };

    private static DataGridViewComboBoxColumn Choice(string property, string header, int width, string[] items)
    {
        var column = new DataGridViewComboBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            Width = width,
            FlatStyle = FlatStyle.Flat,
        };

        column.Items.AddRange(items);
        return column;
    }

    /// <remarks>
    /// Starts hidden and appears only when there is something to report. Left permanently visible
    /// it is an empty white box between the grid and the buttons, which reads as another input the
    /// operator is supposed to fill in.
    /// </remarks>
    private static TextBox BuildProblemBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        Height = 76,
        Margin = new Padding(0, 8, 0, 0),
        Visible = false,
        TabStop = false,
    };

    private ComboBox BuildPresetPicker()
    {
        var picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };

        foreach (HotkeyPreset preset in _presets.Load())
        {
            picker.Items.Add(new PresetChoice(preset));
        }

        if (picker.Items.Count == 0)
        {
            picker.Items.Add("(no presets installed)");
            picker.Enabled = false;
        }

        picker.SelectedIndex = 0;
        return picker;
    }

    // ------------------------------------------------------------------ behaviour

    private void LoadRows(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings)
    {
        _suppressValidation = true;
        _rows.Clear();

        foreach ((string gesture, HotkeyBindingConfig binding) in bindings.OrderBy(b => b.Key, HotkeyGestureOrder.Instance))
        {
            _rows.Add(new BindingRow
            {
                Hotkey = gesture,
                Send = binding.Send ?? string.Empty,
                Action = binding.Action ?? string.Empty,
                Label = binding.Label ?? string.Empty,
                Level2Index = binding.Level2Index == 0 ? string.Empty : binding.Level2Index.ToString(),
            });
        }

        _suppressValidation = false;
    }

    private void OnLoadPreset(object? sender, EventArgs e)
    {
        if (_presetPicker.SelectedItem is not PresetChoice choice)
        {
            return;
        }

        // Replacing rather than merging, and said out loud. A preset is a complete layout; merging
        // would leave the operator with a hybrid neither they nor the preset's author designed.
        DialogResult confirm = MessageBox.Show(
            this,
            $"Replace all {_rows.Count} row(s) with the {choice.Preset.Bindings.Count} from "
            + $"\"{choice.Preset.Name}\"?\n\nNothing is written until you choose Save & Apply.",
            "Load preset",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (confirm is DialogResult.OK)
        {
            LoadRows(choice.Preset.Bindings);
            Revalidate();
        }
    }

    /// <summary>
    /// Saves what is in the grid as a named preset, without applying it.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from Save &amp; Apply. Naming a layout and arming it are different
    /// decisions - the common case for this button is capturing a layout you are part-way through
    /// building, or snapshotting the current one before trying something else. Coupling them would
    /// mean you could not keep a copy without also making it live.
    /// </remarks>
    private void OnSaveAsPreset(object? sender, EventArgs e)
    {
        _grid.EndEdit();
        Revalidate();

        if (Result.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to save yet.", "Save preset", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<HotkeyPreset> existing = _presets.Load();
        using var dialog = new PresetNameDialog(PresetNameDialog.SuggestName(existing.Select(p => p.Name)));

        if (dialog.ShowDialog(this) is not DialogResult.OK)
        {
            return;
        }

        (bool exists, bool isShipped) = _presets.Describe(dialog.PresetName);

        if (exists && !isShipped)
        {
            DialogResult replace = MessageBox.Show(
                this,
                $"A preset named \"{dialog.PresetName}\" already exists. Replace it?",
                "Save preset",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (replace is not DialogResult.Yes)
            {
                return;
            }
        }

        string? error = _presets.TrySave(dialog.PresetName, dialog.PresetDescription, Result, overwrite: true);

        if (error is not null)
        {
            MessageBox.Show(this, error, "Save preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RefreshPresets(dialog.PresetName);

        MessageBox.Show(
            this,
            $"Saved \"{dialog.PresetName}\" with {Result.Count} hotkey(s).\n\n"
            + "This has not changed your active hotkeys - use Save & Apply for that.\n\n"
            + $"Presets are files in:\n{_presets.UserPresetDirectory}",
            "Save preset",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>Reloads the picker from disk and selects <paramref name="select"/> if present.</summary>
    private void RefreshPresets(string? select)
    {
        _presetPicker.Items.Clear();

        IReadOnlyList<HotkeyPreset> presets = _presets.Load();

        foreach (HotkeyPreset preset in presets)
        {
            _presetPicker.Items.Add(new PresetChoice(preset));
        }

        if (_presetPicker.Items.Count == 0)
        {
            _presetPicker.Items.Add("(no presets installed)");
            _presetPicker.Enabled = false;
            _presetPicker.SelectedIndex = 0;
            return;
        }

        _presetPicker.Enabled = true;

        int index = select is null
            ? 0
            : Math.Max(0, presets.ToList().FindIndex(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase)));

        _presetPicker.SelectedIndex = index;
    }

    /// <summary>
    /// Sets one of the two key columns for a row, by press or by typing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method for both columns, because the difference between them is a single flag - which
    /// vocabulary the value is in - and everything around it is identical: suppress dispatch,
    /// forward intercepted presses, write back, revalidate. Two near-identical copies is how the
    /// forwarding came to be wired into one of them and not the other.
    /// </para>
    /// <para>
    /// Dispatch is suppressed for the whole time the dialog is open, in a <c>using</c> so it is
    /// restored even if the dialog throws. Without that, pressing a key in order to record it
    /// would instead fire whatever it is bound to - and most rows here place orders. Forwarding
    /// matters equally: a chord this process already holds never reaches a focused window, so
    /// without it the dialog would hang on the modifiers for exactly the keys already configured.
    /// </para>
    /// </remarks>
    private void CaptureInto(int index, bool hotkey)
    {
        if (_captureRegisteredPresses is null)
        {
            MessageBox.Show(
                this,
                "Capture is unavailable because hotkey dispatch cannot be suspended, so pressing a "
                + "key here could fire its binding instead of recording it.",
                AppInfo.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        BindingRow row = _rows[index];
        string? captured;

        using (var dialog = new KeyCaptureDialog(hotkey ? row.Hotkey : row.Send, hotkey))
        using (_captureRegisteredPresses(dialog.AcceptCapturedGesture))
        {
            captured = dialog.ShowDialog(this) is DialogResult.OK ? dialog.CapturedExpression : null;
        }

        if (captured is null)
        {
            return;
        }

        if (hotkey)
        {
            row.Hotkey = captured;
        }
        else
        {
            row.Send = captured;

            // Action and Send are mutually exclusive, and choosing a shortcut says which is
            // wanted. Clearing the other is less surprising than saving and then being told the
            // row sets both.
            row.Action = string.Empty;
        }

        _rows.ResetItem(index);
        Revalidate();
    }

    /// <summary>
    /// Rehearses everything a trading key does except the keystroke, and reports what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is written into the dialog rather than shown in a message box, because a
    /// successful test ends with Chrome in front of this window by design. A modal raised at that
    /// moment would either be hidden behind Chrome or would have to fight it for the foreground,
    /// and the operator would be reading a dialog instead of seeing the thing it is describing.
    /// The line is still here when they come back.
    /// </para>
    /// <para>
    /// <c>async void</c> because this is an event handler; nothing can await it. Every failure is
    /// caught below rather than escaping onto the message loop.
    /// </para>
    /// </remarks>
    private async void OnTestTargeting(object? sender, EventArgs e)
    {
        if (_runTargetingTest is null)
        {
            return;
        }

        // Disabled for the duration. The command is queued and runs on one consumer, so a second
        // press would queue a second test behind the first rather than achieving anything sooner.
        _test.Enabled = false;
        ShowTestResult("Testing...", Color.FromArgb(70, 70, 70));

        try
        {
            CommandResult result = await _runTargetingTest().WaitAsync(TestTimeout);

            if (result.Outcome is CommandOutcome.Succeeded)
            {
                // The total only. The full breakdown includes a dispatch figure that is always
                // zero here, which reads as a stage that failed rather than one never attempted;
                // the log has the breakdown for anyone chasing latency.
                ShowTestResult(
                    "Targeting works. The Warrior SIM page was found, its Level 2 & Order Entry panel "
                    + "was selected, and the Chrome window was brought to the front in "
                    + $"{result.Timings.Total.TotalMilliseconds:0}ms. Nothing was sent.",
                    Color.FromArgb(20, 110, 60));

                return;
            }

            ShowTestResult(
                "Targeting failed, so a hotkey would not have reached the SIM: "
                + (result.FailureReason ?? "no reason was reported."),
                Color.FromArgb(150, 35, 35));
        }
        catch (TimeoutException)
        {
            ShowTestResult(
                $"The test did not finish within {TestTimeout.TotalSeconds:0} seconds. The bridge may be "
                + "shutting down; otherwise check the log.",
                Color.FromArgb(150, 35, 35));
        }
        catch (Exception ex)
        {
            // A resilience boundary, as elsewhere in the tray: a failed test must leave the
            // dialog usable rather than take the message loop down with it.
            ShowTestResult($"The test could not be run: {ex.Message}", Color.FromArgb(150, 35, 35));
        }
        finally
        {
            // Guarded because the dialog can be closed while a test is in flight.
            if (!IsDisposed)
            {
                _test.Enabled = true;
            }
        }
    }

    private void ShowTestResult(string text, Color colour)
    {
        if (IsDisposed)
        {
            return;
        }

        _testResult.Text = text;
        _testResult.ForeColor = colour;
        _testResult.Visible = true;
    }

    private void AddRow()
    {
        _rows.Add(new BindingRow());
        Revalidate();

        // Selects the new row without opening the capture dialog. Opening it here would mean a
        // modal appears from an action that only asked for an empty row.
        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = _grid.Rows[^1].Cells[3];
        }
    }

    /// <summary>
    /// Copies the selected row, which is how most rows after the first get made.
    /// </summary>
    /// <remarks>
    /// A deck layout is largely the same shape repeated - same modifier, adjacent key, similar
    /// label - so starting from a copy is less work than starting from blank, and it replaces the
    /// Add-then-retype cycle the toolbar buttons encouraged.
    /// </remarks>
    private void DuplicateRow()
    {
        if (_grid.CurrentRow is not { Index: >= 0 and var index } || index >= _rows.Count)
        {
            return;
        }

        BindingRow source = _rows[index];

        _rows.Insert(index + 1, new BindingRow
        {
            // Deliberately not the hotkey: two rows on the same key is a duplicate the resolver
            // would reject, and leaving it blank says plainly that this is the part to set.
            Hotkey = string.Empty,
            Send = source.Send,
            Action = source.Action,
            Label = source.Label,
            Level2Index = source.Level2Index,
        });

        _grid.CurrentCell = _grid.Rows[index + 1].Cells[0];
        Revalidate();
    }

    private void RemoveRow()
    {
        if (_grid.CurrentRow is { Index: >= 0 and var index } && index < _rows.Count)
        {
            _rows.RemoveAt(index);
            Revalidate();
        }
    }

    /// <summary>
    /// Re-runs the real resolver over the edited rows.
    /// </summary>
    /// <remarks>
    /// The same <see cref="HotkeyBindingResolver"/> the bridge uses at startup, not a second
    /// copy of the rules written for the dialog. A validator that disagreed with the loader would
    /// let the operator save something the bridge then silently refused to register.
    /// </remarks>
    private void Revalidate()
    {
        if (_suppressValidation)
        {
            return;
        }

        Dictionary<string, HotkeyBindingConfig> candidate = [];
        List<string> problems = [];

        foreach (BindingRow row in _rows)
        {
            if (row.IsEmpty)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Hotkey))
            {
                problems.Add($"A row with Send '{row.Send}' has no hotkey.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Level2Index)
                && !int.TryParse(row.Level2Index, out _))
            {
                problems.Add($"Hotkey '{row.Hotkey}' has a Level 2 number that is not a whole number.");
                continue;
            }

            if (candidate.ContainsKey(row.Hotkey.Trim()))
            {
                problems.Add($"Hotkey '{row.Hotkey}' appears more than once.");
                continue;
            }

            candidate[row.Hotkey.Trim()] = row.ToConfig();
        }

        HotkeyBindingResolution resolution = HotkeyBindingResolver.Resolve(candidate);
        problems.AddRange(resolution.Problems);

        // Warnings, not problems: each is legal and will register, so blocking would refuse
        // something Windows is perfectly willing to do. They stand out from the notes because
        // one of them - taking a bare letter globally - is a trap nobody chooses on purpose.
        foreach (HotkeyBinding binding in resolution.Bindings)
        {
            if (binding.Gesture.DescribeGlobalCaptureRisk() is { } risk)
            {
                problems.Add("Warning: " + risk);
            }

            if (binding.Action.Keys is { } keys && PlaywrightKeys.DescribeAmbiguity(keys) is { } warning)
            {
                problems.Add("Note: " + warning);
            }
        }

        _problems.Text = string.Join(Environment.NewLine, problems);
        _problems.Visible = problems.Count > 0;

        bool blocking = problems.Any(p =>
            !p.StartsWith("Note:", StringComparison.Ordinal)
            && !p.StartsWith("Warning:", StringComparison.Ordinal));

        // Three levels, because they mean different things: red is "this will not register",
        // amber is "this registers and you will regret it", grey is "worth knowing".
        _problems.ForeColor = blocking
            ? Color.FromArgb(150, 35, 35)
            : problems.Any(p => p.StartsWith("Warning:", StringComparison.Ordinal))
                ? Color.FromArgb(150, 90, 0)
                : Color.FromArgb(70, 70, 70);

        _save.Enabled = !blocking && resolution.Bindings.Count > 0;
        _summary.Text = resolution.Bindings.Count switch
        {
            0 => "No valid hotkeys - nothing would be registered.",
            1 => "1 hotkey.",
            var n => $"{n} hotkeys.",
        };

        Result = candidate;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _grid.EndEdit();
        Revalidate();

        if (!_save.Enabled)
        {
            return;
        }

        int dispatching = Result.Count(b => !string.IsNullOrWhiteSpace(b.Value.Send));

        // The one confirmation in this dialog, because this is the moment keys become live. Every
        // binding with a Send value will place real orders the next time it is pressed.
        DialogResult confirm = MessageBox.Show(
            this,
            $"Apply {Result.Count} hotkey(s), of which {dispatching} send a shortcut to the SIM?\n\n"
            + "These keys become live immediately. Use Test targeting afterwards to confirm the SIM "
            + "is being aimed at, without sending anything.",
            "Apply hotkeys",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        if (confirm is DialogResult.OK)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private sealed record PresetChoice(HotkeyPreset Preset)
    {
        public override string ToString() =>
            Preset.IsUserSupplied ? $"{Preset.Name} (yours)" : Preset.Name;
    }

    /// <summary>
    /// One grid row. Strings throughout, including the numeric column, so a half-typed value is
    /// something the operator can see and correct rather than something the grid rejects mid-keystroke.
    /// </summary>
    private sealed class BindingRow
    {
        public string Hotkey { get; set; } = string.Empty;

        public string Send { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Level2Index { get; set; } = string.Empty;

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Hotkey)
            && string.IsNullOrWhiteSpace(Send)
            && string.IsNullOrWhiteSpace(Action)
            && string.IsNullOrWhiteSpace(Label);

        public HotkeyBindingConfig ToConfig() => new()
        {
            Send = string.IsNullOrWhiteSpace(Send) ? null : Send.Trim(),
            Action = string.IsNullOrWhiteSpace(Action) ? null : Action.Trim(),
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            Level2Index = int.TryParse(Level2Index, out int index) ? index : 0,
        };
    }
}
