using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Asks the operator a yes/no question about startup, and reports a failure to act on it.
/// </summary>
/// <remarks>
/// <para>
/// This exists purely to put a seam in front of <see cref="MessageBox"/>. Without it the whole
/// answer-to-action mapping - which result counts as yes, which button is focused, whether a
/// failed registry write is allowed to be recorded as a decision - lives inside a delegate that
/// no test can enter without putting a modal dialog on screen during a test run.
/// </para>
/// <para>
/// That mapping is the safety-relevant half of the feature. The rule it enforces is that only an
/// explicit yes turns startup back on; everything else, including a dialog dismissed by any other
/// means, leaves the operator's existing choice alone. A rule that cannot be tested is a rule
/// that survives exactly as long as nobody reformats the code around it.
/// </para>
/// </remarks>
internal interface IStartupPrompt
{
    /// <summary>Asks whether startup should be re-enabled after an update.</summary>
    /// <returns>True only on an explicit yes.</returns>
    bool AskToReEnableAfterUpdate(string version);

    /// <summary>Tells the operator that acting on their answer failed.</summary>
    void ReportEnableFailed(string reason);
}

/// <inheritdoc />
internal sealed class MessageBoxStartupPrompt : IStartupPrompt
{
    public bool AskToReEnableAfterUpdate(string version)
    {
        DialogResult answer = MessageBox.Show(
            $"{AppInfo.ProductName} has been updated to version {version}.\n\n" +
            "Starting with Windows is currently switched off, so the bridge will not be " +
            "running - and your hotkeys will not work - until you launch it yourself.\n\n" +
            "Would you like it to start with Windows again?\n\n" +
            "You can change this at any time from the notification-area icon.",
            AppInfo.ProductName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,

            // No is focused deliberately. This dialog can appear unbidden after an update, and
            // the operator's recorded choice was off - so the answer that changes nothing must be
            // the one a reflexive Enter or Space produces.
            MessageBoxDefaultButton.Button2);

        // Only an explicit yes. Written as a positive test rather than "is not No" so that any
        // future result which is neither - a dialog closed by some other route - continues to
        // mean "leave the operator's choice alone".
        return answer is DialogResult.Yes;
    }

    public void ReportEnableFailed(string reason) =>
        MessageBox.Show(
            $"{AppInfo.ProductName} could not switch Start with Windows back on.\n\n" +
            $"{reason}\n\n" +
            "Startup is still off. You can try again from the notification-area icon.",
            AppInfo.ProductName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
}
