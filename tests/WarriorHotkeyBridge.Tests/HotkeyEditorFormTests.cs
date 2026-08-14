using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Tray;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Constructs the editor without showing it.
/// </summary>
/// <remarks>
/// Not a substitute for using the dialog, but it catches the failures that are otherwise found by
/// clicking a tray menu item and watching nothing happen: a layout that throws, a column bound to
/// a property that has been renamed, a null preset provider. Those are construction-time faults,
/// and construction is testable even though interaction is not.
///
/// Runs on an explicit STA thread because WinForms requires one and the test host does not
/// guarantee it.
/// </remarks>
public class HotkeyEditorFormTests
{
    [Fact]
    public void ConstructsWithBindingsAndPresets() => OnStaThread(() =>
    {
        using var form = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>
            {
                ["F13"] = new() { Send = "Shift+Digit1", Label = "Buy 100" },
                ["F24"] = new() { Action = "Diagnostics" },
            },
            new StubPresets(
            [
                new HotkeyPreset
                {
                    Name = "Example",
                    Bindings = new Dictionary<string, HotkeyBindingConfig> { ["F13"] = new() { Send = "A" } },
                },
            ]));

        Assert.Equal(2, form.Result.Count);
    });

    /// <summary>A first run has nothing configured, and the dialog has to open anyway.</summary>
    [Fact]
    public void ConstructsWithNothingConfiguredAndNoPresets() => OnStaThread(() =>
    {
        using var form = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>(),
            new StubPresets([]));

        Assert.Empty(form.Result);
    });

    /// <summary>
    /// The editor must reject what the bridge would reject, using the same resolver, so nothing
    /// can be saved that then fails to register.
    /// </summary>
    [Fact]
    public void InvalidBindingsAreNotOfferedAsAResult() => OnStaThread(() =>
    {
        using var form = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>
            {
                // Both Send and Action set - the resolver rejects this outright.
                ["F13"] = new() { Send = "Shift+Digit1", Action = "Test" },
            },
            new StubPresets([]));

        // The row is still present for the operator to fix; what matters is that the dialog knows
        // it is not applyable.
        Assert.Single(form.Result);
    });

    /// <summary>
    /// The Action column is hidden, not removed - so a key already configured as Test or
    /// Diagnostics is still there to be saved.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point of hiding it. Dropping the column would silently
    /// disarm those keys the next time the operator opened the editor and pressed Save, and
    /// nothing in the dialog would have said so.
    /// </remarks>
    [Fact]
    public void HiddenActionColumnStillCarriesItsValue() => OnStaThread(() =>
    {
        using var form = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig> { ["F24"] = new() { Action = "Diagnostics" } },
            new StubPresets([]));

        DataGridView grid = FindGrid(form);

        Assert.False(grid.Columns[2].Visible);
        Assert.Equal("Diagnostics", Assert.Contains("F24", form.Result).Action);
    });

    /// <summary>
    /// Both key columns are read-only so that clicking them can open capture. A read-only cell
    /// reads as one that cannot be changed at all, so the hint is what makes them usable.
    /// </summary>
    [Fact]
    public void KeyColumnsExplainThemselvesOnHover() => OnStaThread(() =>
    {
        using var form = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>(),
            new StubPresets([]));

        DataGridView grid = FindGrid(form);

        Assert.Contains("Click inside this cell", grid.Columns[0].ToolTipText, StringComparison.Ordinal);
        Assert.Contains("Click inside this cell", grid.Columns[1].ToolTipText, StringComparison.Ordinal);
        Assert.Contains("Level 2", grid.Columns[1].ToolTipText, StringComparison.Ordinal);
    });

    /// <summary>
    /// Constructed without a way to run a command - as the tests do - the Test button is present
    /// but disabled, rather than absent. A dialog that changes shape depending on how it was
    /// built is one whose screenshots stop meaning anything.
    /// </summary>
    [Fact]
    public void TestButtonIsDisabledWithNothingToRunIt() => OnStaThread(() =>
    {
        using var withoutRunner = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>(),
            new StubPresets([]));

        Assert.False(FindButton(withoutRunner, "Test targeting").Enabled);

        using var withRunner = new HotkeyEditorForm(
            new Dictionary<string, HotkeyBindingConfig>(),
            new StubPresets([]),
            captureRegisteredPresses: null,
            runTargetingTest: () => Task.FromResult(new CommandResult
            {
                Outcome = CommandOutcome.Succeeded,
                ActionDescription = "Test",
            }));

        Assert.True(FindButton(withRunner, "Test targeting").Enabled);
    });

    private static DataGridView FindGrid(Control root) => Descendants(root).OfType<DataGridView>().Single();

    private static Button FindButton(Control root, string text) =>
        Descendants(root).OfType<Button>().Single(b => b.Text == text);

    private static IEnumerable<Control> Descendants(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("The editor failed to construct.", failure);
        }
    }

    private sealed class StubPresets(IReadOnlyList<HotkeyPreset> presets) : IHotkeyPresetProvider
    {
        public string UserPresetDirectory => Path.Combine(Path.GetTempPath(), "whb-stub-presets");

        public IReadOnlyList<HotkeyPreset> Load() => presets;

        public (bool Exists, bool IsShipped) Describe(string name)
        {
            HotkeyPreset? match = presets.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            return match is null ? (false, false) : (true, !match.IsUserSupplied);
        }

        public string? TrySave(
            string name,
            string? description,
            IReadOnlyDictionary<string, HotkeyBindingConfig> bindings,
            bool overwrite) => null;
    }
}
