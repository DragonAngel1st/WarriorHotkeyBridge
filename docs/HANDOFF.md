# Handoff

Written for a fresh assistant conversation with no prior context. Everything here was learned the
expensive way; most of it is not visible from the code alone.

**Last updated:** version 1.1.2, 338 tests passing.

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

Working tree clean, local == remote, 338 tests, builds with warnings as errors.

**Shipped and working:** hotkeys with reclaim-on-conflict retry; warm CDP connection with watchdog,
zombie detection and sleep/resume recovery; single-round-trip Level 2 probe (~9 ms steady state);
tray status and diagnostics; start-with-Windows owned by the app; per-user WiX MSI; Stream Deck
Go/Stop shortcuts and button art; hotkey editor with presets and key capture.

**Verified against a live SIM session.** Real trades executed end to end in 26–47 ms.

**Released:** `v1.0.0` and `v1.1.1` on GitHub, the latter with the MSI attached and marked latest.
1.1.0 was never released; the version was bumped to 1.1.1 rather than reinstalled over itself, which
also sidesteps the same-version upgrade trap below. 1.1.1 is installed and signed off on locally.

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

### The SIM's charts are iframes, and focus beats selection
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

The fix is in `Level2Controller`: the probe reports `focusInFrame` on the same round trip as
everything else, and preparation blurs the focused frame to return focus to the page, then re-probes
to verify. `Level2Result.RefusedIfFocusTrapped()` is the invariant — Ready and focus-trapped must
never be dispatchable together, on any path. Verified against a live TradingView iframe before
shipping: `iframe` → blur → `body`.

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
dotnet test                               # 338 tests
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

**v1.2.0 — multiple Level 2 panels.** Currently a chord goes to the first panel in DOM order
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
