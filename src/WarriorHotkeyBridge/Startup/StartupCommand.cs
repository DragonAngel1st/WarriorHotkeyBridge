namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Formats and parses the command string stored in the Run key.
/// </summary>
/// <remarks>
/// Kept pure and separate from registry access so the quoting and comparison rules - the parts
/// that actually go wrong - can be unit tested. Every entry in a real Run key that has a path
/// containing spaces is quoted; an unquoted one breaks the moment the install path does.
/// </remarks>
internal static class StartupCommand
{
    /// <summary>Builds the value to store for an executable path.</summary>
    public static string Format(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        // Always quoted, even when the current path happens to have no spaces: the installed
        // location is chosen by the installer, and "Program Files" or a user name with a space
        // would otherwise silently produce a command Windows parses as several arguments.
        return $"\"{executablePath.Trim('"')}\"";
    }

    /// <summary>
    /// Extracts the executable path from a stored command, ignoring any arguments.
    /// </summary>
    public static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        // Unquoted: Windows takes everything up to the first space as the executable. A path
        // with spaces stored unquoted is already broken, and treating it as a mismatch is the
        // right outcome - it means the entry will not launch us.
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>Whether a stored command points at the given executable.</summary>
    public static bool PointsAt(string? command, string executablePath)
    {
        string? registered = ParseExecutablePath(command);

        if (registered is null || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registered),
                Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed stored path cannot match anything, and must not throw during a
            // routine status read.
            return false;
        }
    }
}
