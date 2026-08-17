using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Clears the state a troubleshooting session wants gone, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Exists so "reinstall it cleanly" is one action rather than a set of instructions relayed down a
/// telephone. It removes the things that accumulate and could plausibly be stale - the dedicated
/// Chrome profile, logs, diagnostics reports, the startup preference - and leaves the two things
/// that are the operator's own work.
/// </para>
/// <para>
/// <b>Configuration and Presets are never removed.</b> Those hold hand-made trading bindings and
/// saved layouts that may exist nowhere else, and this runs from an installer on a machine whose
/// owner cannot be talked through a recovery. The list of what goes is an explicit allowlist of
/// named folders resolved from <see cref="AppPaths"/> - never a wildcard, never the root - so a
/// mistake here can delete a log folder and nothing worse.
/// </para>
/// <para>
/// Before deleting anything it writes the live bindings into Presets as a timestamped preset. That
/// is belt and braces: Configuration survives regardless, but a snapshot the operator can reload
/// from the editor costs nothing and turns "my keys are gone" from a catastrophe into a dropdown.
/// </para>
/// </remarks>
internal static class SessionReset
{
    /// <summary>
    /// Runs the reset. Never throws: it is called from an installer, where an exception would
    /// surface as a failed install of an application that is otherwise fine.
    /// </summary>
    /// <returns>Human-readable lines describing what happened, for the console and the log.</returns>
    public static IReadOnlyList<string> Run(AppPaths paths, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);

        List<string> report = [];

        report.Add(SnapshotBindings(paths, now));

        // The allowlist. Configuration and Presets are absent on purpose and must stay absent;
        // Deck too, because the installer owns those shortcuts and re-creates them itself.
        foreach ((string label, string path) in new[]
        {
            ("Chrome profile", paths.ChromeProfile),
            ("logs", paths.Logs),
            ("diagnostics reports", paths.Diagnostics),
            ("startup preference", paths.State),
        })
        {
            report.Add(Remove(label, path));
        }

        report.Add($"Kept: your hotkeys ({paths.Configuration}) and your presets ({paths.Presets}).");

        return report;
    }

    /// <summary>
    /// Copies the live bindings into Presets so they can be reloaded from the editor.
    /// </summary>
    /// <remarks>
    /// Reads the user configuration as a document rather than binding it to options, so a file
    /// carrying "//" comment keys or a hand-edited oddity still yields whatever bindings it has
    /// instead of failing wholesale.
    /// </remarks>
    private static string SnapshotBindings(AppPaths paths, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(paths.UserConfigFile))
            {
                return "No configuration file to snapshot; nothing was bound yet.";
            }

            JsonNode? root = JsonNode.Parse(
                File.ReadAllText(paths.UserConfigFile),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

            if (root?["Hotkeys"]?["Bindings"] is not JsonObject bindings || bindings.Count == 0)
            {
                return "No hotkey bindings to snapshot.";
            }

            var preset = new JsonObject
            {
                ["name"] = $"Backup before reset {now.LocalDateTime:yyyy-MM-dd HH:mm}",
                ["description"] = "Written automatically before a clean reinstall. Load it from the "
                    + "hotkey editor to put these keys back.",
                ["bindings"] = bindings.DeepClone(),
            };

            Directory.CreateDirectory(paths.Presets);

            string file = Path.Combine(
                paths.Presets,
                $"backup-before-reset-{now.LocalDateTime:yyyyMMdd-HHmmss}.json");

            File.WriteAllText(file, preset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return $"Snapshotted {bindings.Count} binding(s) to {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not fatal, and deliberately not a reason to abandon the reset: Configuration is not
            // being deleted, so the bindings survive in place whether or not this copy succeeds.
            return $"Could not snapshot the bindings ({ex.Message}); your configuration file is untouched.";
        }
    }

    private static string Remove(string label, string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return $"No {label} to remove.";
            }

            Directory.Delete(path, recursive: true);
            return $"Removed the {label}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by a process that has not finished exiting is the ordinary cause.
            // Reporting and carrying on beats failing an install over a log file.
            return $"Could not remove the {label}: {ex.Message}";
        }
    }
}
