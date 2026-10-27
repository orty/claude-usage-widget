# Claude Usage for the PowerToys Dock

The Claude Usage widget's numbers on the **PowerToys Command Palette Dock**,
for **several Claude accounts at once**: every account gets a Dock band of its
own, and one more band shows them all side by side.

```
 ┌────────────────────────────────────────────────────────────── Dock ──┐
 │  ⌂  WinGet │            │ Work 42%         Personal 7%   │  CPU  13:45 │
 │            │            │ resets in 1h 12m resets in 4h  │             │
 └──────────────────────────────────────────────────────────────────────┘
```

Selecting a band opens the account's page in Command Palette with the three
bars of the widget's normal mode: the session (5 h), the week (7 d) and the
per-model weekly limit, with their reset times.

## What "the PowerToys bar" is

PowerToys has no bar of its own outside **Command Palette**. Since PowerToys
0.98, Command Palette has a **Dock**: a toolbar pinned to an edge of the screen
(it reserves the space with the AppBar API, like the taskbar) that shows
*bands* provided by Command Palette extensions. That is the only extensible
bar PowerToys offers, and the one this targets. See
[Command Palette Dock](https://learn.microsoft.com/windows/powertoys/command-palette/dock)
and [Adding Dock support](https://learn.microsoft.com/windows/powertoys/command-palette/adding-dock-support).

What the Dock can and cannot do decides what this looks like:

- A band shows **text** (a title and a subtitle) and an icon, and repaints
  when they change. It cannot draw the widget's coloured bars, so the band
  says `Work 42%`, optionally with a text meter (`Work ▰▰▱▱▱ 42%`) or all three
  limits (`Work 42% · 17% · 8%`). The bars themselves are on the account's page.
- The Dock asks an extension for its bands **without any context** (no
  monitor, no instance), and shows a pinned band the same on every monitor.
  So "an instance per account" is "a band per account": add accounts in
  Command Palette, then pin the bands you want, where you want them.

## How it stays aligned with upstream

The hard constraint of this branch is that merging
[niccolo-sabato/claude-usage-widget](https://github.com/niccolo-sabato/claude-usage-widget)
keeps working, with its fixes and features, without conflicts. Three rules
hold it:

1. **Nothing of upstream's is edited.** The branch only adds `powertoys/` and
   `.github/workflows/powertoys-dock.yml`. The `upstream-untouched` CI job fails
   the moment any other file differs from upstream.
2. **Nothing of upstream's is copied.** Command Palette extensions are C#, the
   widget is Python. Instead of porting how the widget reads the usage (the
   endpoints, the Claude Code login and its saved copies, the rate-limit pause,
   the rotation of session keys, the error messages), the extension runs a
   small Python *bridge* ([bridge/claude_usage_bridge.py](bridge/claude_usage_bridge.py))
   that loads upstream's own `src/widget.pyw` as a module, the way upstream's
   `cc_cli.py` does, and calls the same `fetch_usage` the widget calls on every
   refresh. The package carries `src/` as it is at build time, linked from the
   project file, so a merge of upstream reaches the Dock at the next build.
3. **What the bridge needs of upstream is written down and tested.**
   `UPSTREAM_API` in the bridge lists every name it uses, and
   [bridge/tests/test_contract.py](bridge/tests/test_contract.py) checks them,
   with their signatures, against the `src/` in the tree. A rename upstream
   fails CI instead of the Dock.

```
 Command Palette ──COM──▶ ClaudeUsageDock.exe (C#) ──stdin/stdout JSON──▶ python claude_usage_bridge.py
   Dock bands               bands, pages, timer                              import src/widget.pyw
                                                                             widget.fetch_usage(...)
```

### Syncing with upstream

```powershell
git remote add upstream https://github.com/niccolo-sabato/claude-usage-widget.git   # once
git fetch upstream
git checkout main
git merge --ff-only upstream/main      # or "Sync fork" on GitHub
git push origin main
git checkout powertoys
git merge main                          # merge, not rebase: the branch is shared
python -m unittest discover -s powertoys/bridge/tests   # the contract with upstream
git push
```

Then build and deploy the extension again (below). If the contract test
fails, upstream renamed or changed something the bridge calls: fix the bridge,
never upstream's file. To try an upstream change without rebuilding, point the
extension's **Widget source folder** setting at a clone's `src` folder.

## Accounts, keys and logins

The accounts live with the bridge, in the extension's own folder
(`accounts.json`, written with upstream's own atomic, backed-up config
writer). Three ways to add one, from **Claude usage** in Command Palette:

- **Add the account Claude Code is signed in as.** Reads with the OAuth login
  Claude Code keeps on the PC, which renews itself. Nothing to paste.
- **Add an account with a session key** from the browser, as in the widget.
- **Import accounts from the Claude Usage widget.** Takes the names and
  organizations of the widget's accounts and their Claude Code link.

Two rules come from how claude.ai and Claude Code behave, and are upstream's
own rules:

- **Never share a session key with the widget.** claude.ai renews a session
  key as it is used and retires the old one. Two programs holding the same key
  take turns killing each other's copy. That is why the import leaves the keys
  behind, and why the add form asks for a key of its own (sign in again, or in
  another browser profile, to get a second one). The Claude Code login is not
  rotated by reading, so it is shared freely.
- **One Claude Code login per PC.** An account other than the one signed in
  is read with the copy of its login the widget saves when it switches Claude
  Code between accounts (`"Claude Usage.exe" --cc save NAME`, or the switch in
  the widget's account list). Without one, its band says `CC elsewhere`
  rather than show another account's numbers under its name.

## Requirements

- Windows 10 2004 / Windows 11, **PowerToys 0.98 or later**, Command Palette
  enabled, and **Enable Dock** turned on in Command Palette settings.
- **Python 3.10+** with tkinter (the python.org installer has it) and
  **Pillow**: `py -3 -m pip install pillow`. Upstream's module imports both
  even though nothing is drawn. The installed widget's `Claude Usage.exe`
  bundles its own Python, which another program cannot use.
- To build: only the **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`).
  Visual Studio is optional (see below).

## Build, install, pin

1. From the repository folder, in PowerShell:

   ```powershell
   powershell -ExecutionPolicy Bypass -File powertoys\scripts\install.ps1
   ```

   [scripts/install.ps1](scripts/install.ps1) builds the package with
   `dotnet build`, signs it and installs it with `Add-AppxPackage`. No Visual
   Studio, no Windows SDK, no Developer Mode. The first time, it creates a
   self-signed certificate `CN=ClaudeUsageDock` in your own certificate store
   (its private key cannot be exported) and Windows asks **once** for
   administrator rights, to trust it for app packages (Local Machine >
   Trusted People). Every later run is an **update**: the version comes from
   the clock, so the accounts and settings survive it. Run it again after
   each merge of upstream. `-Platform ARM64` builds for ARM.

   With Visual Studio 2022 17.14+ instead (*.NET desktop development* and
   *Windows application development* workloads, Developer Mode on): open
   `powertoys/ClaudeUsageDock/ClaudeUsageDock.csproj`, pick x64 or ARM64, then
   **Build > Deploy ClaudeUsageDock**. Keep to one way per PC: a Visual
   Studio deploy registers an unsigned folder, the script installs a signed
   package, and switching may need the old one removed first
   (`Get-AppxPackage ClaudeUsageDock | Remove-AppxPackage`, which also deletes
   its accounts).
2. In Command Palette, run **Reload** (*Reload Command Palette extensions*).
3. Type **Claude usage**, add your accounts.
4. Right-click the Dock, **Edit Dock**, **+**, and add the *Claude usage: …*
   bands you want, one per account, or *Claude usage: every account*.
   Right-click a band to show or hide its subtitle.

Settings (Command Palette settings > Extensions > Claude Usage): refresh
interval (5 min by default; the Claude Code endpoint is rate limited, so keep
3 min or more with several accounts), what the band shows, the Python to use,
and the widget source folder.

## Troubleshooting

Logs are in the extension's settings folder: `extension.log` (the C# side) and
`bridge\bridge.log` (upstream's own log, written by the bridge). The bridge can
be run by hand with the same code the Dock runs:

```powershell
py -3 powertoys\bridge\claude_usage_bridge.py --data-dir $env:TEMP\cu-test --once hello
py -3 powertoys\bridge\claude_usage_bridge.py --data-dir $env:TEMP\cu-test --once add_claude_code
py -3 powertoys\bridge\claude_usage_bridge.py --data-dir $env:TEMP\cu-test --once accounts
```

| On the band | Meaning |
|---|---|
| `key expired` | The session key is no longer accepted: add a new one from the account's menu. |
| `run claude` | The Claude Code login expired: run `claude` once. |
| `CC elsewhere` | Claude Code is signed in as another account and no saved login of this one is usable. |
| `paused` | The Claude Code endpoint answered 429; the last numbers stay and it tries again later. |
| `bridge error` | Python could not run the widget's code: check Python, Pillow and the settings. |

If the extension never shows up and Windows says *"Windows cannot access the
specified device, path, or file"* for `ClaudeUsageDock.exe`, Microsoft
Defender's attack surface reduction rule *Block executable files unless they
meet a prevalence, age, or trusted list criterion*
(`01443614-cd74-433a-b99e-2ecdc07bfc25`) is blocking it: a build of your own
is new and rare by definition. Event 1121 in *Microsoft-Windows-Windows
Defender/Operational* confirms it. Exclude the extension from that rule, in an
administrator PowerShell (the folder name changes with each build, hence the
wildcard), then run **Reload**:

```powershell
Add-MpPreference -AttackSurfaceReductionOnlyExclusions 'C:\Program Files\WindowsApps\ClaudeUsageDock_*\ClaudeUsageDock.exe'
```

On a PC whose Defender is managed (Intune, Group Policy), a local exclusion is
ignored: the exclusion has to be added there.

## Tests

```powershell
python -m unittest discover -s powertoys/bridge/tests -v
```

They run upstream's real `fetch_usage`, its Claude Code checks, its key
rotation and its config writer, with only the network faked, in a temporary
home that never touches a real login. On Windows they also start the bridge
as a separate process, as the extension does. CI runs them on Windows,
builds the extension for x64 and ARM64, and runs `install.ps1` twice on a
runner with its Windows SDK hidden, checking that the second run updates the
first without losing its data.
