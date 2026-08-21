# Handoff

Written for a fresh assistant conversation with no prior context. Everything here was learned the
expensive way; most of it is not visible from the code alone.

**Last updated:** version 1.2.8, 359 tests passing.

---

## 1. What this is

A Windows-only C#/.NET 10 tray application. It receives global keyboard hotkeys from a Stream Deck
(F13–F24) and routes them into the **Warrior Trading SIM** web app running in Chrome, over the
Chrome DevTools Protocol via Playwright.

It is a **transport, not a trading model.** It delivers a configured chord to the Level 2 & Order
Entry panel; Warrior SIM's own hotkey settings decide what that chord means. It holds no notion of
"buy" or "sell", deliberately — a second copy of those semantics could silently disagree with the
first. `Label` exists so the operator can read the log; the bridge never interprets it.

| | |
|---|---|
| Repo | <https://github.com/DragonAngel1st/WarriorHotkeyBridge> (public, MIT, default branch `main`) |
| Local | `C:\Users\admin\atlas-projects\Warrior Sim Hotkey Connector` |
| Owner | Patrick Miron (`DragonAngel1st`), open source, free, no charge ever |
| Installed to | `%LOCALAPPDATA%\Programs\WarriorHotkeyBridge\` (per-user, no admin) |
| User data | `%LOCALAPPDATA%\WarriorHotkeyBridge\` — Configuration, Logs, State, Presets, Deck, ChromeProfile |

Note the two paths differ only by `Programs\`. Confusing them has already caused one round of
misunderstanding.

## 2. Two rules that shape everything

**Fail closed.** Nothing is dispatched unless the page URI *Host* equals `sim.warriortrading.com`
exactly (parsed, `OrdinalIgnoreCase`, never `Contains("warrior")`), the title matches, and Level 2
is present. A normal Warrior setup also has `chatroom.warriortrading.com` open, which any substring
test would wrongly accept.

**Dispatch exactly once, never retried.** Preparation may retry freely; the keystroke is attempted
once. A Playwright call that reports a timeout *may still have delivered the key*, so retrying could
place a second order. A missed command is recoverable by pressing again; a duplicated one is not.
`IsPossiblyDelivered` distinguishes "Unknown key" (definitively not sent) from a timeout (possibly
sent) and reports them differently.

## 3. Current state

Working tree clean, local == remote, 380 tests, builds with warnings as errors.

**Shipped and working:** hotkeys with reclaim-on-conflict retry; warm CDP connection with watchdog,
zombie detection and sleep/resume recovery; single-round-trip Level 2 probe (~9 ms steady state);
tray status and diagnostics; start-with-Windows owned by the app; per-user WiX MSI; Stream Deck
Go/Stop shortcuts and button art; hotkey editor with presets and key capture.

**Verified against a live SIM session.** Real trades executed end to end in 26–47 ms.

**Released:** through `v1.2.10` on GitHub, each with the MSI attached.

`v1.2.9` carries the wake-up click — the fix for the failure the user hit for days, keystrokes
silently doing nothing after working in another window. Confirmed on the live session before it was
written, by performing the click by hand, and **confirmed again in live trading on 2026-08-21**.

`v1.2.10` accepts both SIM hosts, after Warrior moved the dashboard to `sim2` overnight and every
key stopped working. Installed and verified against the live session the same morning: the bridge
finds `sim2` from the shipped default, with no override in the user's file.

## 4. Traps — read this before touching anything

These cost real time or real damage. They are not obvious and several bit twice.

### MSI: same-version upgrades silently ship the old binary
Windows Installer decides whether to copy a **versioned** file by comparing *file versions*, not
contents. Every managed assembly is versioned. With `RemoveExistingProducts` scheduled *after* the
file copy, `WarriorHotkeyBridge.dll` at 1.0.0.0 does not replace 1.0.0.0 — the installer reports
success and leaves the old build. **Measured: an install returning exit code 0 left an assembly nine
hours stale.** File hashes do not rescue this; MSI consults them only for *unversioned* files.

`Schedule="afterInstallValidate"` (remove first) is therefore mandatory, and the trade-off is
documented at length in the `.wxs`. The reviewers' concern — a failed upgrade leaves nothing
installed — is real but far less bad than silently running unreplaced code.

### Verify the DLL, never the EXE
`WarriorHotkeyBridge.exe` is the .NET **apphost**, a fixed native stub identical across builds.
Comparing its hash proves nothing. Always compare `WarriorHotkeyBridge.dll`:

```powershell
$a = (Get-FileHash "$env:LOCALAPPDATA\Programs\WarriorHotkeyBridge\WarriorHotkeyBridge.dll" -Algorithm SHA256).Hash
$b = (Get-FileHash "artifacts\publish\WarriorHotkeyBridge\release_win-x64\WarriorHotkeyBridge.dll" -Algorithm SHA256).Hash
$a -eq $b
```

### An MSI can reboot the machine
A silent (`/qn`) install that hits locked files restarts Windows **with no prompt**. This happened —
`msiexec` returned 1641 and took the machine down mid-session. The package now sets
`REBOOT=ReallySuppress`, and any manual test install should pass it too. A per-user app touching no
system file never legitimately needs a reboot.

### `--` is illegal inside an XML comment
Bit twice: once in a `.csproj` describing `--debug`, once in the `.wxs` describing `--quit`. Write
"the quit switch" instead. Costs a full build cycle each time.

### WinForms traps, all hit in one session
- A `Label` eats `&` as a mnemonic prefix. "Level 2 & Order Entry" renders as "Level 2  Order Entry".
  Set `UseMnemonic = false`.
- A docked `Label` with a fixed `Height` silently clips. An `AutoSize` label only wraps if something
  bounds its width — set `MaximumSize` on the container's `Resize`.
- `FlowLayoutPanel.WrapContents` defaults to true and folds a button row into a vertical stack. Off
  for OK/Cancel; **on** for toolbars, or a button silently vanishes past the edge.
- `ProcessCmdKey`, not `KeyDown`, for key capture. Tab, Enter, Escape and the arrows are consumed as
  navigation before any `KeyDown` handler runs.
- A form focuses the first control in tab order on `Show`. Anything set up in the constructor that
  depends on focus must be redone in `OnShown`.
- `DataGridView.AllowUserToAddRows` renders a permanent blank row that reads as a stray record.

### Installing clears stale state — by an allowlist, never a wildcard
Every install runs `WarriorHotkeyBridge.exe --reset --silent` between `InstallFinalize` and the
launch, so "reinstall it cleanly" is one double-click rather than instructions relayed down a
telephone to someone who cannot follow them. It removes the **Chrome profile, logs, diagnostics
reports and the startup preference**.

**It must never remove `Configuration` or `Presets`.** Those hold live trading bindings and saved
layouts that may exist nowhere else, and this runs from an installer on a machine whose owner cannot
be talked through a recovery. `SessionReset` deletes an explicit allowlist of four folders resolved
from `AppPaths` — never a wildcard, never the root — so the worst a mistake here can do is cost a
log folder. It also snapshots the live bindings into `Presets\backup-before-reset-<stamp>.json`
first, which is belt and braces: `Configuration` survives regardless, but a copy the operator can
reload from a dropdown turns "my keys are gone" into a non-event.

`SKIPCLEAN=1` does a straight in-place update:
`msiexec /i WarriorHotkeyBridge-Setup-x64.msi SKIPCLEAN=1`. Clean is the default deliberately, to
rule stale state out of a diagnosis; flip it once that stops being the common case.

`--reset` is handled **before logging is configured**, because it deletes the log directory and a
configured rolling sink would be holding a file inside it.

### `SetForegroundWindow` reports success without raising anything
Windows refuses a foreground steal from a process that does not already own the foreground. It
flashes the taskbar button instead — and **the call still returns true**. The bridge is a background
tray application, so every raise attempted while the operator was in another window was declined,
reported as successful, and logged nothing.

It stayed hidden because the Level 2 window was normally already in front. Running a **scanner in a
second Chrome window** is what exposed it: click a ticker there, press a trading key, and the chord
was delivered to a page whose window never came forward — so the SIM ignored it and no order row
appeared at all, not even a rejection.

`RaiseToForeground` does two things about it. It attaches our input queue to the current foreground
thread for the duration of the call, which is the long-standing Win32 remedy for the restriction.
Then it **ignores the return value and asks `GetForegroundWindow` what actually happened** — a raise
that was silently declined is now logged as a failure instead of being reported as success. Trusting
that return value is what hid this for a week.

The fast path is unchanged in cost: if the window is already foreground it returns after a single
`GetForegroundWindow` and calls nothing else.

**Latency, measured over 60 live commands** — worth knowing before optimising the wrong thing:
targeting 36.2 ms (73%), dispatch 9.3 ms (19%), activation 3.8 ms (8%), **everything else including
all logging 0.06 ms (0%)**. Turning logging off would buy nothing; the cost is CDP round trips.

### The SIM host can change overnight, and did
On 2026-08-21 Warrior moved the SIM from `sim.warriortrading.com` to **`sim2.warriortrading.com`**
with no notice. Every key stopped working. The bridge was behaving perfectly: hotkeys registered,
Chrome connected, CDP talking — and no page it was permitted to touch, because the host gate is an
**exact** match and the dashboard was one character off.

The log said `No open page has host sim.warriortrading.com`, which was true and almost useless: the
page was open, right there, on a host the message did not mention. Reading Chrome's own target list
(`curl http://127.0.0.1:9222/json/list`) is what found it in under a minute — **that is the first
thing to do whenever the bridge says it cannot find the SIM.**

`AllowedHosts` is now a list, and both hosts ship accepted. Three things about it that matter:

- **Still exact, per entry.** A suffix or wildcard test on `warriortrading.com` would have survived
  the move — and would also let a chord reach the chatroom, which the operator has open every day.
  One morning of downtime is the cheaper side of that trade.
- **`AllowedHost` (singular) is the emergency lever**, and it **adds** rather than replaces. It is
  one line an operator can be talked through over the phone the next time this happens, without
  waiting for a release. Additive is deliberate: a line left in the user's file after the fix ships
  must not quietly narrow the bridge to a host that has since been abandoned — that is precisely
  how the F23/F24 bindings went wrong.
- **`AllowedHosts` (plural) replaces**, so an operator who deliberately narrows the list gets what
  they asked for; an empty or blank one falls back to the built-in list rather than to nothing,
  because refusing everything is safe but presents as "the bridge stopped working" with no clue why.

The failure message now names every accepted host.

### Raising the window does not wake the SIM — only a trusted click does
This is the sequel to the section above, and it is the harder half. Getting the Chrome window in
front is necessary and **not sufficient**: the SIM decides whether it is the active application by
listening for real focus events, and a window raise fires none. As far as the renderer is
concerned that page never lost focus — so no `focus`, no `focusin`, and the SIM goes on ignoring
every shortcut until it receives one.

Symptom: click in *anything* else — a second Chrome window, a text selection on another page,
VS Code — then press a trading key. Nothing happens. Nothing at all: no order, no rejection, no
error. Manually clicking the Level 2 panel fixes it until the next time you leave.

**Everything the bridge could measure said it was working**, which is what made this take days:
right page, chord delivered to `<body>`, `document.hasFocus()` true, `defaultPrevented` false,
correct `key` and `code`, arriving at the exact millisecond of the `SENT` line. A capture-phase
listener on the top document *saw the keystroke arrive*. The page received it and chose to do
nothing with it.

Four hypotheses died to measurement before the real one: the window raise (it worked — verified
with `GetForegroundWindow`), the chart iframe (`activeElement` was `body`), `document.hasFocus()`
(true), and text selection on another page (removing it changed nothing). Each was killed rather
than built on.

**A synthesised `FocusEvent` does not work.** It was tried on the live page and the SIM ignored it;
it checks `isTrusted`. Only a real interaction counts, which is why `Level2Controller.ReactivateAsync`
performs a Playwright click on the **tab header** — the one element in a panel made almost entirely
of order controls that is safe to click.

This also explains the intermittency, and the explanation is worth keeping. When Level 2 was *not*
the selected component, the bridge clicked that same tab to select it and woke the SIM **by
accident**. When it was already selected, the click was skipped — and so, unknowingly, was the
wake-up. The bug therefore appeared to depend on which panel you had last touched, which is why it
looked random.

The trigger is deliberately narrow: `ActivationOutcome` distinguishes **`AlreadyInFront`** from
**`Raised`** and **`NotRaised`**, and the wake-up runs only for the latter two. Having had to touch
the foreground *is* the signal that the operator was elsewhere. Press after press within the SIM
costs nothing extra — that path returns after a single `GetForegroundWindow`.

Cost when it does run: one `EvaluateAsync` hit test, one forced click, one focus release — roughly
20 ms, once, on the first press after coming back. The hit test exists to avoid the alternative:
Playwright's checked click waits for the page to stop repainting and measured ~200 ms on a live
dashboard. The main probe deliberately does **not** run that hit test on the already-selected path,
because `getBoundingClientRect` forces a layout reflow and that path runs on every command; the
wake-up pays for its own reflow instead of charging every keystroke for it.

Two invariants that must survive any rewrite here. The bridge clicks the **tab header and nothing
else** — `Level2Result.HasClickableTab` is false when the panel has been popped out into its own
window, and a missed wake-up costs one keystroke while a stray click costs a position. And the
wake-up **never fails a command**: if the click cannot be delivered it logs and sends the chord
anyway, because the page may well have been awake already.

### The installer must never own the presets folder
`%LOCALAPPDATA%\WarriorHotkeyBridge\Presets` is created by **`AppPaths.CreateAndEnsure`**, never by the
MSI. An MSI that creates a directory owns it, and an owned directory is a candidate for removal on
uninstall *and* during the remove-then-install half of every upgrade — which this package performs
on each install, since `RemoveExistingProducts` is scheduled `afterInstallValidate`. Presets are
hand-made work that must outlive the product, so the installer is kept away from that path
entirely; the `.wxs` carries a comment saying so.

It is created eagerly rather than on first save because it is somewhere the operator is *told* to
put files — restoring a backup, or carrying a layout between machines. A folder that only appears
after you have saved a preset is no use to someone who already has one and nowhere to put it.

### The shipped appsettings must bind no keys
Configuration layers merge **per key, not per object**. A binding shipped in the application's own
`appsettings.json` and rebound in the operator's user file merges into a single entry carrying BOTH
`Send` and `Action` — which the resolver rejects. So rebinding a shipped key silently costs the
operator that key, and the shipped default reappears at every restart, overwriting their edit.

F23 and F24 shipped as Test and Diagnostics on the assumption that nobody would rebind spare keys.
Someone did. Both actions are reachable from the UI — Test targeting in the editor, Run Diagnostics
in the tray — so neither needs to cost a deck key. `ShippedBindingsTests` reads the file that
actually ships and fails if any binding is added back.

### The bridge is armed or parked, and sign-in no longer arms it
`SessionState` is the on/off switch. **Armed**: hotkeys registered, Chrome launched and maintained.
**Parked**: hotkeys released so F13–F24 are free for other applications, Chrome left alone, tray
icon showing the off glyph. `BridgeStatus.Parked` outranks every other rule *except* Starting —
a parked bridge legitimately has no hotkeys and no connection, which the fault rules would
otherwise report as an Error the operator just asked for.

This exists because "Chrome is running" used to be an invariant of the process being alive:
`ChromeConnectionWorker` called `EnsureRunningAsync` on **every watchdog pass**, so closing the
browser by hand simply brought it back and a stop button was not expressible.

- Sign-in registers `"...exe" --parked` (`StartupCommand.ParkedSwitch`). A manual launch still
  arms — someone double-clicking the app wants to use it.
- `--start` / `--park` signal a resident instance through `SessionSignal`, two named events with
  one handler, copying `ShutdownSignal` exactly. **`--stop` was not reused**: it is an existing
  alias for `--quit` and repurposing it would silently change every shortcut already using it.
- The deck's *Stop Trading* shortcut is now `--park --silent`, not `--quit --close-chrome`.
  Quitting released the hotkeys but took the tray icon with it, so there was nothing left to show
  the bridge was off and nothing to press to bring it back.
- **Chrome is found, not assumed.** `ChromeOptions.ExecutablePath` defaults to the 64-bit Program
  Files location, which is one of at least four. A 32-bit install is under Program Files (x86); a
  per-user install - no administrator needed, so the kind on a machine set up for someone else - is
  under the user AppData. `ChromeLauncher.CandidateExecutables` tries the `App Paths` registry entry
  first (authoritative, and the only one that finds a custom location) then the three literal paths.
  A configured path that exists always wins, so naming a specific channel still works. Before this,
  Start simply could not work on such a machine and the only remedy was hand-editing JSON.
- **Arming is idempotent, and that is load-bearing.** `ArmAsync` returning early when already armed
  skipped the Chrome launch, so pressing Go Trading with the session armed but the browser closed
  did nothing at all - the state most in need of the button. Registration is skipped on that path;
  the launch is not.
- **`Chrome:AutoLaunch` does not gate Start.** It governs one thing only: whether the watchdog puts
  Chrome back if it disappears mid-session. It is off by default so closing the browser yourself
  keeps it closed. Gating the explicit request on it shipped in 1.2.0 and made a fresh install's
  Start button silently do nothing — the starter config ships the Chrome block commented out, so
  `AutoLaunch` was false and `LaunchOnRequestAsync` returned before launching. Pressing Start *is*
  the permission; the setting answers "may the bridge act on its own initiative", which is a
  different question.
- **An upgrade rewrites its own sign-in entry.** A Run value written by an older build names the
  same executable, so `PointsAt` reads it as healthy and every other check leaves it alone — and it
  would go on launching without `--parked`, arming a session at every sign-in. The service compares
  the whole command against `ExpectedCommand` and rewrites silently: it is a repair, not a decision.
- `SessionController` orders it deliberately: arming registers hotkeys **before** launching Chrome
  so the deck is live immediately; parking flips the state **before** closing the browser, or the
  watchdog relaunches it in the gap and the stop button appears to do nothing.

### Tray artwork has to be a glyph, not an app badge
The icon is 16×16 at 100% DPI. The first artwork was a rounded-square badge — keyboard, wifi arcs,
candlesticks, glowing frame — and at 16px it rendered as a coloured smudge; the dark "off" variant
was near-invisible on the Windows 11 taskbar, which is dark by default. Automatic rescue failed
too: the badge's outer frame glows as brightly as the glyph inside it, so no luminance threshold
separates them.

What works is one high-contrast shape on transparency, cropped to its content. `assets/icons/` holds
the shipped 256px `capturing-on.png` / `capturing-off.png`; the untouched originals live in
`assets/icons/source/` and are **not** shipped (the csproj glob is non-recursive on purpose).
Crop matters: the keyboard glyph is 2:1 in a square canvas, so a quarter of the height was padding
before it was cropped — at 16px that padding is the difference between a shape and a smear.

`TrayIconFactory` composes at runtime: artwork scaled to the current small-icon size, plus a corner
status dot for Degraded/Error/WaitingForChrome only. Ready gets no dot deliberately — a permanent
green one is one the eye stops seeing, which would make the amber and red harder to notice. Every
load failure falls back to the drawn dot, because a tray app that cannot produce an icon has no
menu and therefore no way to reach anything.

### Focus beats selection - a chart frame OR a text field
The charts are TradingView widgets in `blob:` iframes — four on a normal dashboard. **Clicking
inside one moves browser keyboard focus to that frame**, and CDP delivers a dispatched chord to the
*focused frame*. Level 2 then never sees the key: TradingView takes the first printable character
as the start of a symbol search, so `Shift+Digit3` opened a search box containing `#`.

The trap is that **every other signal says everything is fine.** FlexLayout runs in the parent
document and never sees a click that lands inside a frame, so `flexlayout__tabset-selected` stays
on Level 2. The probe reported Ready, the tab click was correctly skipped as unnecessary
(`target 4.4ms`), and the command logged OK — while the order never happened. Selection and focus
are independent, and only focus decides which *document* receives the key.

Diagnosed by measurement, not reasoning: three separate theories were wrong first. The decisive
evidence was a capture-phase `keydown` listener on the top document recording **nothing** for a
keystroke the bridge had definitely dispatched — a listener on `document` cannot miss a key
delivered to that document, so the key had gone to another one. `document.activeElement` in the top
document was an `<iframe>`, and Playwright's frame tree showed `document.hasFocus()` true inside it.

**It is not only iframes.** The same failure happens with a focused **text field**, and that one is
harder to see: Level 2 genuinely selected, tabset reporting "Level 2 & Order Entry", and
`document.activeElement` an `input` — an order-entry box inside the panel holding the caret.
`Shift+Digit3` became a `#` typed into that box. The first version of the guard tested only for
`tagName === 'IFRAME'`, so this sailed through every check; it shipped in 1.2.1 and was found in
1.2.2. **The rule is about anything that consumes the chord, not about frames.**

`FocusTrapPredicate` in `Level2Controller` is the single definition — `IFRAME`, `INPUT`, `TEXTAREA`,
`SELECT`, `isContentEditable` — interpolated into *both* the probe and the repair, because having
the test in one and the fix in the other is exactly how the input case was missed.

Preparation blurs whatever holds focus, then re-probes to verify.
`Level2Result.RefusedIfFocusTrapped()` is the invariant — Ready and focus-trapped must never be
dispatchable together, on any path. Verified live before shipping, on a real order-entry field
holding a share size: `input` → blur → `body`, **value unchanged**. That last part matters — a
bridge that cleared a share size on the way to placing an order would be worse than the bug.

Two reporting faults fell out of this and are fixed too. The Level 2 selection lines were **Debug**
while the operator runs at Information, so the log could not say whether targeting had done anything
— the one fact needed to diagnose it. And the success line said `OK` when it only ever meant
*dispatched*; it now says `SENT`, because the bridge cannot observe which component acted.

### The Action column is hidden, and hiding is not removing
`Action` (Test / Diagnostics) is no longer shown in the editor: Test is a button in the toolbar and
Diagnostics is a tray menu item, so the column was a step every operator had to understand purely
in order to leave it alone. It is `Visible = false`, **not** deleted — deleting it would drop the
value on save and silently disarm the Test and Diagnostics keys of anyone who already has them
bound. A row whose only payload is a hidden Action renders its Sends cell as a grey
`(Test - sends nothing)` via `CellFormatting`, so it does not read as half-finished.

The Test button runs the same `HotkeyActionKind.Test` action through the same `CommandQueue`, not
against the executor directly. A second consumer could re-target the page while a trading command
was part-way through selecting a component on it. `CommandQueue.EnqueueAsync` carries a
`TaskCompletionSource` so the outcome comes back to the window; the queue completes it as
*Rejected* if the channel is already closed, or the caller would wait forever at shutdown.

Its result is written into a label in the dialog, never a message box. A passing test ends with
Chrome in front of the editor **by design**, so a modal raised at that moment would be either
hidden behind Chrome or fighting it for the foreground.

### Registered hotkeys never reach a focused window
Windows delivers a registered hotkey as `WM_HOTKEY` to the *registering* window. A dialog waiting on
ordinary keyboard input is therefore blind to exactly the keys already configured. Capture needs
**both** paths: forwarded `WM_HOTKEY` for keys the bridge holds, ordinary input for keys it does
not. Wiring only one is how the Sends dialog came to hang on the modifiers.

**Never unregister-and-re-register to capture.** Global hotkeys are exclusive and first-come, so any
gap lets another application take a trading key permanently. The forwarding approach has no gap.

### `Environment.GetFolderPath` ignores `%LOCALAPPDATA%`
It asks the Windows shell. A test that "isolates" itself by setting the environment variable is
still operating on the live profile. `AppPaths.CreateAndEnsure(root)` exists for this; the original
tests nearly wrote a starter config over real trading bindings.

### Shift+1 and Shift+Digit1 are different
Measured against Chrome. `Shift+1` delivers `event.key === "1"` with shift held — which a real
keyboard never produces. `Shift+Digit1` delivers `"!"`, as hardware does. Both give
`event.code === "Digit1"`. The bridge warns rather than rewrites, because silently changing which
character a trading shortcut delivers is worse than an explanatory line. **Key capture exists
largely to make this automatic.**

### Other measured facts
- `innerText` forces a synchronous layout reflow. Using it in the probe took ~9 ms to ~187 ms on a
  live dashboard. Always `textContent`.
- `flexlayout__tab_button--selected` is **per tabset** (11 present at once). Exactly one element
  document-wide carries `flexlayout__tabset-selected`, on `flexlayout__tabset_tabbar_outer`. That is
  the authoritative "this component receives the keyboard" signal.
- CDP `Page.bringToFront` switches the tab but does **not** raise the OS window. Win32
  `SetForegroundWindow` is also required.
- Chrome reports window geometry in DIPs; a per-monitor-aware process sees physical pixels. At 150%
  scaling they disagree, which is why window matching scores size separately from origin.
- Playwright `CloseAsync()` only *detaches* on a CDP connection. Closing needs a raw `Browser.close`
  CDP command.
- All Warrior auth cookies are session-only. The session cannot be persisted; the SIM URL also needs
  a per-session `hash` minted at sign-in, so it cannot be navigated to cold.

### Tooling
- `Directory.Build.props` has several `PropertyGroup`s — use XPath, not dotted property access.
- The test project inherits the app's `Presets/` folder via project reference, so `Load()` returns
  shipped presets too. Filter on `IsUserSupplied`.
- xUnit: `Assert.Single(x.Where(...))` is an analyzer **error** — use the filtering overload.
  `Assert.Throws` with a block lambda that only throws is ambiguous with the async overload; assign
  to an explicit `Action` first.
- The PowerShell sandbox blocks `Remove-Item` if a `$env:PATH = "C:\Program Files\..."` assignment
  appears in the same script. Split them, or use Bash `rm`.

## 5. The technique that works

**Render WinForms to PNG and look at it.** Every UI bug in this project was found this way and none
were found by re-reading code. Write a temporary xUnit test on an STA thread that shows the form
off-screen at `(-4000, -4000)`, calls `DrawToBitmap`, saves a PNG, then read the image. Delete the
harness afterwards — screenshot tests rot.

Render *both* the clean state and the failing state. The failing render is what proves error text is
legible and that the Save button actually disables.

## 6. How to build, verify and ship

```powershell
dotnet build                              # warnings are errors
dotnet test                               # 380 tests
pwsh -File installer/Build-Installer.ps1  # publish + MSI -> artifacts/installer/
```

Install and verify in one go — **always check the DLL hash and the hotkey count**:

```powershell
$msi = (Resolve-Path "artifacts\installer\WarriorHotkeyBridge-Setup-x64.msi").Path
Start-Process msiexec -ArgumentList "/i","`"$msi`"","/qn","REBOOT=ReallySuppress" -Wait
```

WiX is pinned to **5.0.2** deliberately: v6/v7 require accepting the Open Source Maintenance Fee
EULA before they will build anything, and this project must stay buildable by anyone who clones it.
v4.0.0–4.0.4 carry three HIGH CVEs; 5.0.2 audits clean. The `.wxs` uses the v4 schema namespace that
v4–v7 all share, so moving to v7 later needs no source change.

## 7. Outstanding

**Nothing is blocking a release.** v1.1.1 is tagged and released with the MSI attached; the user
signed off on the editor after running the installed 1.1.1 build.

**Presets are the user's to author**, through the editor's **Copy preset...** button, not written by
an assistant. This covers Ross's Sim Default and the user's own full-deck mapping. The 15-key deck
has only twelve F13–F24 available, so three keys need modifiers (`Ctrl+F13`…); key capture handles
those.

**v1.3.0 — multiple Level 2 panels.** Currently a chord goes to the first panel in DOM order
(`Level2Index` defaults to 0), which is deterministic but not predictable, since rearranging the
layout changes which is index 0. The `Level2Index` column was removed from the editor for that
reason. Two SIM *pages* are already correctly refused as ambiguous; two *panels* in one page are not.

**The user's design, in their words: send to the panel matching the currently selected component's
ticker — or the last selected component's ticker if nothing is selected right now — and use the
SIM's own colour link between charts and components to establish which panel that is.**

Both halves are the platform stating its own intent rather than the bridge guessing at layout. The
colour link is the structural half: a blue-linked Level 2 matches a blue-linked chart, and changing
the ticker on one changes the other. The selection-and-ticker rule is the half that decides *which*
link group the operator means at the moment they press a key — which is the part index-in-DOM can
never answer, because it does not change when the operator's attention does.

Two things already known that this work will run into:
- `flexlayout__tab_button--selected` is **per tabset** and there are 11 at once. Exactly one element
  document-wide carries `flexlayout__tabset-selected`, on `flexlayout__tabset_tabbar_outer`. That is
  the authoritative "this component receives the keyboard" signal — and therefore the starting point
  for "currently selected".
- "Last selected if none is selected now" needs state that survives between commands. There is no
  such store today; `Level2Controller` is stateless per command by design. Whatever holds it must not
  become a second source of truth that can disagree with the page — prefer re-reading the page and
  falling back to a remembered ticker only when the page genuinely says nothing is selected.

**User's decisions, untouched:**
- `C:\Users\admin\.git` is a repo rooted at the **home directory** with zero commits, no remote and
  an empty index — so nothing is tracked *yet*. There is no `.gitignore` and `.git/info/exclude` is
  the stock template, so `.ssh`, `.git-credentials`, `atlas_deploy_key` and `certificates/` are all
  unignored: one `git add -A` run from that directory would stage private keys. Flagged repeatedly;
  the user's call. **Do not touch it without being asked.**
- Code signing. The MSI is unsigned, so SmartScreen warns. Needs a paid certificate — a spend
  decision. The build accepts a signing step without restructuring.

**Not started:** optional auto-off timer; per-command latency histograms; replacing Playwright with
a direct CDP WebSocket client (~100 MB of the 212 MB install is the bundled Node runtime).

## 8. Working with this user

- **Wants no dead code.** IDE0051/IDE0052/CS8321 are build errors now; keep them that way.
- **Reports UI problems precisely and is usually right.** When they say something "doesn't work",
  read the log before theorising — twice the log contradicted the obvious explanation.
- **Check the log at** `%LOCALAPPDATA%\WarriorHotkeyBridge\Logs\bridge-YYYYMMDD.log`. Command lines
  are bracketed `>>>>>>>>>> HOTKEY` / `<<<<<<<<<< OK` with a latency breakdown.
- Their config is **live and armed** — real trading bindings. Back it up before editing it, never
  overwrite it casually, and note the app owns it (the MSI must never write it).
- They use a **15-key Soomfon/MiraBox deck**, US keyboard layout, mixed-DPI monitors at 150%.
- Prefers being told the trade-off and given a recommendation over being handed a survey.

## 9. Corrections made in earlier sessions

Recorded so they are not repeated as fact:
- An MSI "byte-identical to the build" claim was made by comparing apphosts. It proved nothing.
- A claim that WiX's file hashes prevent skipped copies was wrong — hashes govern unversioned files
  only, and that error caused the stale-assembly bug.
- A claim that `~/.ssh/config` had duplicated stanzas was a misreading of `Select-String -Context`
  output, which repeats overlapping lines. The file was fine. **Open the file; don't trust context
  windows.**
- `F23` (Test) originally skipped window activation while claiming to prove the whole path. It now
  activates. Diagnostics deliberately does not.
