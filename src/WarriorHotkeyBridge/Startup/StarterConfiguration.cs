namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Writes a commented example configuration the first time the bridge runs.
/// </summary>
/// <remarks>
/// <para>
/// A fresh install has no trading bindings at all - only the two shipped defaults, which send
/// nothing. That is deliberate: an installer that put live buy and sell bindings on a stranger's
/// machine would be reckless. It does mean the application does nothing useful until the operator
/// authors a configuration file, and learning a format from documentation before the first success
/// is a poor way to begin.
/// </para>
/// <para>
/// The template resolves that without weakening the rule. Every binding in it is commented out, so
/// the file is inert as written: it changes no behaviour, and no keystroke can be delivered until
/// the operator has read a line, understood it, and deliberately uncommented it.
/// </para>
/// <para>
/// It is written once, only when no configuration file exists. It is never repaired, upgraded or
/// rewritten - once the file exists it belongs to the operator, and an application that edits a
/// file holding live trading bindings is not one worth trusting.
/// </para>
/// </remarks>
internal static class StarterConfiguration
{
    /// <summary>
    /// Creates the template if no user configuration exists.
    /// </summary>
    /// <returns>True when a file was written.</returns>
    public static bool TryWrite(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            if (File.Exists(paths.UserConfigFile))
            {
                return false;
            }

            Directory.CreateDirectory(paths.Configuration);

            // WriteAllText rather than an atomic replace: File.Exists has just reported that
            // nothing is there, and losing a race with another writer would only mean the
            // operator's own file wins, which is the correct outcome anyway.
            File.WriteAllText(paths.UserConfigFile, Template);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A convenience, not a requirement. The bridge runs perfectly well without it, so
            // failing to write it must never be allowed to affect startup.
            return false;
        }
    }

    /// <summary>
    /// The file written on first run.
    /// </summary>
    /// <remarks>
    /// Uses "//" keys rather than JSON comments. The configuration binder accepts both, but a
    /// "//" key survives being loaded and re-saved by an editor or a future settings UI, whereas
    /// a comment does not - and these notes are the only documentation present at the moment the
    /// operator most needs it.
    /// </remarks>
    private const string Template = """
        {
          "//": "Warrior Hotkey Bridge - your configuration. This file overrides the appsettings.json shipped next to the executable, and survives upgrades and uninstall. Everything below is commented out, so as written this file changes nothing.",

          "// HOW TO USE": "Rename a key from '// F13' to 'F13' to activate it. Restart the bridge afterwards - configuration is read once at startup.",

          "// WHAT Send MEANS": "Send delivers a keyboard chord to the Level 2 & Order Entry panel. It does NOT decide what happens. Warrior SIM's own hotkey settings decide that, and you maintain them there. Label is free text for the log and tray only; the bridge never interprets it.",

          "// SHIFT AND DIGITS": "Use Shift+Digit1, not Shift+1. Measured against Chrome: Shift+Digit1 delivers event.key '!' exactly as a physical Shift+1 does, while Shift+1 delivers '1'. If your SIM shortcut reads event.code either spelling works.",

          "// KEYS THAT CANNOT BE SENT": "F13-F24 have no browser key mapping - they work as the trigger on the left, never as the Send value on the right. Numpad keys carry their navigation meaning unshifted (Numpad1 arrives as End), so use Digit1 for a digit.",

          "// EXAMPLES": "Copy any of these into the Bindings object below, keeping the F-key as the name. They are held out here on purpose: every name inside Bindings is read as a hotkey, so an example left in there would be reported at startup as an unparseable key rather than ignored.",

          "// EXAMPLE F13": { "Send": "Shift+Digit1", "Label": "describe what your SIM does with Shift+1" },
          "// EXAMPLE F14": { "Send": "Shift+Digit2", "Label": "describe it here - the bridge only logs this" },
          "// EXAMPLE F15": { "Send": "Control+KeyQ", "Label": "a Ctrl chord" },

          "// ALREADY BOUND": "F23 and F24 come bound by the shipped defaults to Test and Diagnostics. Both send nothing and are always safe to press. Press F23 first: it exercises the whole targeting pipeline without delivering a keystroke, so a success proves everything except the dispatch itself.",

          "Hotkeys": {
            "Bindings": {
            }
          },

          "// ONE BUTTON START": "Uncomment the Chrome block to have the bridge launch its own Chrome, on its own profile, with remote debugging enabled - so a single Stream Deck button starts a trading session. It never touches your ordinary Chrome profile.",

          "// Chrome": {
            "AutoLaunch": true
          }
        }

        """;
}
