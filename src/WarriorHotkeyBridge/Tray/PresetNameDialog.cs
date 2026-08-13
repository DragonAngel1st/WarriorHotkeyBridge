using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Asks for a name and an optional description when saving a preset.
/// </summary>
/// <remarks>
/// WinForms has no input dialog, and the alternatives are worse than twenty lines of layout: a
/// VB InputBox drags in a whole assembly for one prompt and cannot carry a second field or
/// validate as you type.
/// </remarks>
internal sealed class PresetNameDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _description;
    private readonly Button _ok;

    public PresetNameDialog(string suggestedName)
    {
        Text = "Save preset";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 210);

        _name = new TextBox { Text = suggestedName, Dock = DockStyle.Fill };
        _description = new TextBox { Dock = DockStyle.Fill };
        _ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

        _name.TextChanged += (_, _) => _ok.Enabled = _name.Text.Trim().Length > 0;
        _ok.Enabled = _name.Text.Trim().Length > 0;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
        };

        buttons.Controls.AddRange([cancel, _ok]);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Name", AutoSize = true, Margin = new Padding(0, 0, 0, 2) }, 0, 0);
        layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(new Label { Text = "Description (optional)", AutoSize = true, Margin = new Padding(0, 10, 0, 2) }, 0, 2);
        layout.Controls.Add(_description, 0, 3);
        layout.Controls.Add(buttons, 0, 4);

        Controls.Add(layout);

        AcceptButton = _ok;
        CancelButton = cancel;

        Shown += (_, _) => _name.SelectAll();
    }

    public string PresetName => _name.Text.Trim();

    public string? PresetDescription =>
        string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text.Trim();

    /// <summary>Suggests a name that does not collide with something already there.</summary>
    public static string SuggestName(IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string candidate = $"{Environment.UserName}'s layout";

        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (int i = 2; i < 100; i++)
        {
            string numbered = $"{candidate} {i}";

            if (!taken.Contains(numbered))
            {
                return numbered;
            }
        }

        return AppInfo.ProductName + " layout";
    }
}
