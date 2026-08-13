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

    private bool _suppressValidation;

    public HotkeyEditorForm(
        IReadOnlyDictionary<string, HotkeyBindingConfig> current,
        IHotkeyPresetProvider presets)
    {
        ArgumentNullException.ThrowIfNull(current);
        _presets = presets;

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

    private Panel BuildLayout()
    {
        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 58,
            Padding = new Padding(4, 4, 4, 8),
            Text =
                "Each row sends a keyboard shortcut into the Level 2 & Order Entry panel. What that "
                + "shortcut DOES is set in Warrior SIM's own hotkey settings - this only delivers it.\r\n"
                + "Use Shift+Digit1 rather than Shift+1. Leave Send empty and set Action to Test or "
                + "Diagnostics for a key that sends nothing.",
        };

        var presetRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        var loadPreset = new Button { Text = "Load", AutoSize = true };
        loadPreset.Click += OnLoadPreset;
        presetRow.Controls.AddRange([new Label { Text = "Preset:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, _presetPicker, loadPreset]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        var remove = new Button { Text = "Remove row", AutoSize = true };
        remove.Click += OnRemoveRow;
        _save.Click += OnSave;
        buttons.Controls.AddRange([cancel, _save, remove]);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_summary, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        var problemsPanel = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(0, 6, 0, 0) };
        problemsPanel.Controls.Add(_problems);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        host.Controls.Add(_grid);
        host.Controls.Add(problemsPanel);
        host.Controls.Add(bottom);
        host.Controls.Add(presetRow);
        host.Controls.Add(help);

        CancelButton = (Button)buttons.Controls[0];
        return host;
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,
            DataSource = _rows,
        };

        grid.Columns.AddRange(
            TextColumn(nameof(BindingRow.Hotkey), "Hotkey", 110),
            TextColumn(nameof(BindingRow.Send), "Sends into Level 2", 170),
            Choice(nameof(BindingRow.Action), "Action", 110, ["", "Test", "Diagnostics"]),
            TextColumn(nameof(BindingRow.Label), "Label (yours - never interpreted)", 280),
            TextColumn(nameof(BindingRow.Level2Index), "Level 2 #", 80));

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

    private static TextBox BuildProblemBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window,
        ForeColor = Color.FromArgb(160, 40, 40),
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

        _problems.Text = problems.Count == 0 ? string.Empty : string.Join(Environment.NewLine, problems);
        _problems.ForeColor = problems.All(p => p.StartsWith("Note:", StringComparison.Ordinal))
            ? Color.FromArgb(90, 90, 90)
            : Color.FromArgb(160, 40, 40);

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
