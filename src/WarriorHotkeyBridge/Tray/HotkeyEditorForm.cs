using System.ComponentModel;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;

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
/// The hotkey is typed rather than captured by pressing it, which looks like a missed opportunity
/// and is not. The bridge holds F13-F24 globally the entire time this dialog is open, so pressing
/// one would fire the binding rather than record it - the dialog would be competing with the very
/// registration it exists to edit.
/// </para>
/// </remarks>
internal sealed class HotkeyEditorForm : Form
{
    private readonly IHotkeyPresetProvider _presets;
    private readonly BindingList<BindingRow> _rows = [];
    private readonly DataGridView _grid;
    private readonly TextBox _problems;
    private readonly ComboBox _presetPicker;
    private readonly Button _save;
    private readonly Label _summary;
    private Label _help = null!;
    private Button _saveAsPreset = null!;
    private Button _capture = null!;
    private readonly Func<IDisposable>? _suppressDispatch;

    private bool _suppressValidation;

    public HotkeyEditorForm(
        IReadOnlyDictionary<string, HotkeyBindingConfig> current,
        IHotkeyPresetProvider presets,
        Func<IDisposable>? suppressDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _presets = presets;

        // Optional so the form stays constructible in tests without a hotkey service. In the
        // application it is always supplied - see the guard in OnCaptureKey, which refuses to
        // capture rather than capturing with live keys.
        _suppressDispatch = suppressDispatch;

        Text = $"{AppInfo.ProductName} - Hotkeys";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 460);
        Size = new Size(900, 560);
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
                + "shortcut DOES is set in Warrior SIM's own hotkey settings - this only delivers it. "
                + "Use Shift+Digit1 rather than Shift+1. Leave the Sends column empty and pick an "
                + "Action for a key that sends nothing.",
        };

        var loadPreset = new Button { Text = "Load", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        loadPreset.Click += OnLoadPreset;

        _saveAsPreset = new Button { Text = "Save as preset...", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        _saveAsPreset.Click += OnSaveAsPreset;

        _capture = new Button { Text = "Capture key...", AutoSize = true, Margin = new Padding(18, 0, 0, 0) };
        _capture.Click += OnCaptureKey;

        var addRow = new Button { Text = "Add row", AutoSize = true, Margin = new Padding(18, 0, 0, 0) };
        addRow.Click += OnAddRow;

        var removeRow = new Button { Text = "Remove row", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        removeRow.Click += OnRemoveRow;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        toolbar.Controls.AddRange(
        [
            new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 6, 0, 0) },
            _presetPicker,
            loadPreset,
            _saveAsPreset,
            _capture,
            addRow,
            removeRow,
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
            RowCount = 5,
        };

        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // help
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // toolbar
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // grid
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // problems
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons

        host.Controls.Add(_help, 0, 0);
        host.Controls.Add(toolbar, 0, 1);
        host.Controls.Add(_grid, 0, 2);
        host.Controls.Add(_problems, 0, 3);
        host.Controls.Add(bottom, 0, 4);

        // An AutoSize label only wraps if something bounds its width. Without this it lays out as
        // one enormously wide line and the form simply clips it, which is what the fixed height
        // was hiding.
        host.Resize += (_, _) => ConstrainHelpWidth(host);
        ConstrainHelpWidth(host);

        CancelButton = cancel;
        return host;
    }

    private static void ConstrainHelpWidth(TableLayoutPanel host)
    {
        if (host.Controls.Count > 0 && host.Controls[0] is Label help)
        {
            int available = host.ClientSize.Width - host.Padding.Horizontal;
            help.MaximumSize = new Size(Math.Max(120, available), 0);
        }
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
            TextColumn(nameof(BindingRow.Hotkey), "Hotkey", 110),
            TextColumn(nameof(BindingRow.Send), "Sends into Level 2", 180),
            Choice(nameof(BindingRow.Action), "Action", 120, ["", "Test", "Diagnostics"]),
            TextColumn(nameof(BindingRow.Label), "Label (yours - never interpreted)", 300));

        grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

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
    /// Records a shortcut by having the operator press it, and writes it into the selected row.
    /// </summary>
    /// <remarks>
    /// Hotkey dispatch is suppressed for the whole time the capture dialog is open, in a
    /// <c>using</c> so it is restored even if the dialog throws. Without that, pressing a key in
    /// order to record it would instead fire whatever that key is bound to - and most rows here
    /// place orders. If no suppression is available the capture is refused outright rather than
    /// run unprotected.
    /// </remarks>
    private void OnCaptureKey(object? sender, EventArgs e)
    {
        _grid.EndEdit();

        if (_grid.CurrentRow is not { Index: >= 0 and var index } || index >= _rows.Count)
        {
            MessageBox.Show(this, "Select a row first.", "Capture shortcut", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_suppressDispatch is null)
        {
            MessageBox.Show(
                this,
                "Capture is unavailable because hotkey dispatch cannot be suspended. Type the "
                + "shortcut instead.",
                "Capture shortcut",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        BindingRow row = _rows[index];
        string? captured;

        using (_suppressDispatch())
        using (var dialog = new KeyCaptureDialog(row.Send))
        {
            captured = dialog.ShowDialog(this) is DialogResult.OK ? dialog.CapturedExpression : null;
        }

        if (captured is null)
        {
            return;
        }

        row.Send = captured;

        // An Action and a Send are mutually exclusive, and the operator has just said which they
        // want by pressing a key. Clearing the other is less surprising than saving successfully
        // and then being told the row sets both.
        row.Action = string.Empty;

        _rows.ResetItem(index);
        Revalidate();
    }

    private void OnAddRow(object? sender, EventArgs e)
    {
        _rows.Add(new BindingRow());
        Revalidate();

        // Land the caret in the new row's first cell, so adding a row and typing a key is one
        // continuous motion rather than add-then-hunt-for-the-cell.
        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = _grid.Rows[^1].Cells[0];
            _grid.BeginEdit(selectAll: true);
        }
    }

    private void OnRemoveRow(object? sender, EventArgs e)
    {
        if (_grid.CurrentRow is { IsNewRow: false, Index: >= 0 and var index } && index < _rows.Count)
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

        // A warning, not a problem: Shift with a bare digit is legal and delivers a different
        // character than a physical Shift+number. Worth saying, never worth blocking.
        foreach (HotkeyBinding binding in resolution.Bindings)
        {
            if (binding.Action.Keys is { } keys && PlaywrightKeys.DescribeAmbiguity(keys) is { } warning)
            {
                problems.Add("Note: " + warning);
            }
        }

        _problems.Text = string.Join(Environment.NewLine, problems);
        _problems.Visible = problems.Count > 0;
        _problems.ForeColor = problems.All(p => p.StartsWith("Note:", StringComparison.Ordinal))
            ? Color.FromArgb(70, 70, 70)
            : Color.FromArgb(150, 35, 35);

        bool blocking = problems.Any(p => !p.StartsWith("Note:", StringComparison.Ordinal));

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
            + "These keys become live immediately. Press F23 (Test) afterwards to confirm targeting "
            + "without sending anything.",
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
