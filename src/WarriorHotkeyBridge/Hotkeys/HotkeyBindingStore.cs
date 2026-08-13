using Microsoft.Extensions.Options;
using WarriorHotkeyBridge.Configuration;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// The bindings currently in force, which the operator may change while the bridge is running.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is bound once at startup and <see cref="IOptions{TOptions}"/> is immutable, so
/// something has to own "what the hotkeys are right now" once editing them without a restart is
/// possible. This is that thing: seeded from configuration, replaced when the operator saves.
/// </para>
/// <para>
/// Deliberately not <c>IOptionsMonitor</c> with <c>reloadOnChange</c>. File-watch reloading would
/// re-register global hotkeys at whatever moment an editor happened to flush a half-written file -
/// including mid-session, from a background thread, for a file that does not yet parse. Applying
/// changes is instead an explicit act with a known-good set of bindings behind it.
/// </para>
/// </remarks>
internal interface IHotkeyBindingStore
{
    /// <summary>The bindings the next registration will use.</summary>
    IReadOnlyDictionary<string, HotkeyBindingConfig> Current { get; }

    /// <summary>Replaces the set. Does not register anything; the caller decides when to apply.</summary>
    void Replace(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings);
}

internal sealed class HotkeyBindingStore : IHotkeyBindingStore
{
    private volatile IReadOnlyDictionary<string, HotkeyBindingConfig> _current;

    public HotkeyBindingStore(IOptions<HotkeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _current = options.Value.Bindings;
    }

    /// <remarks>
    /// Volatile reference to an immutable snapshot rather than a lock: the UI thread replaces it
    /// wholesale while the command path may be reading it, and swapping one reference is atomic.
    /// A reader either sees the old complete set or the new complete set, never a half-applied mix.
    /// </remarks>
    public IReadOnlyDictionary<string, HotkeyBindingConfig> Current => _current;

    public void Replace(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        // Copied so a caller that keeps mutating its dictionary afterwards cannot change what the
        // bridge believes is registered.
        _current = new Dictionary<string, HotkeyBindingConfig>(bindings, StringComparer.OrdinalIgnoreCase);
    }
}
