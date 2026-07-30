# IpLeash

A WPF desktop app that cuts a list of Windows applications off from the network whenever the
machine's public IP is not the one you expect.

The intended use is a VPN kill-switch: if the tunnel drops and your WAN IP reverts to your ISP
address, every monitored app stops talking to the internet until the expected IP is back.

![The IpLeash window](images/app.png)

## Requirements

- Windows 10/11
- .NET 9 desktop runtime (or the SDK)
- **Administrator rights** — creating firewall rules requires them, so the app is manifested
  `requireAdministrator` and prompts for UAC at launch.

## Build and run

Run `dotnet build` from the repository root, then launch the built `IpLeash.exe` from
`src\IpLeash\bin\Debug\net9.0-windows`. Windows will prompt for elevation.

## Behaviour worth knowing

The window shows the rest. These are the decisions you cannot see by looking at it:

- **Fail closed.** If the public IP cannot be read at all, that is treated as a mismatch and the
  blocks go on. A kill-switch that opens up when it loses confidence is not a kill-switch.
- **One global expected IP** drives every decision, and only the public/WAN IP does — the local
  adapter list is information, not input.
- **Reaction is immediate on any network address change**, not just on the poll interval.
- **Closing the window does not quit.** It hides to the notification area and enforcement carries
  on; a balloon tip says so the first time. **Exit lives only in the tray menu** and is the single
  path that ends enforcement — when something is blocked it names what is about to be released
  first, because that is the one consequential, easy-to-do-by-accident action in the app.
- **Only one instance ever runs.** A second launch surfaces the first window and exits, so
  re-running the exe is a fine way to reopen from the tray. This is not a nicety: a second
  instance's startup cleanup would delete the first's *active* rules, silently unblocking apps
  while the first window still read BLOCKED.
- **The list is editable only while stopped.** Reconciling rules mid-flight buys nothing.
- **The proxy panel warns only when a proxy actually affects the measurement.** A proxy that is
  configured but bypassed for the probe host leaves the reading genuinely yours.

The tray icon is grey when not monitoring, green when the IP matches, red when something is
blocked, and amber when the IP is unknown and blocking fail-closed. Hovering shows the same in
words.

> On Windows 11 a newly registered tray icon starts in the **overflow flyout** behind the `^`
> chevron. Drag it out, or pin it via Settings → Personalization → Taskbar → Other system tray
> icons.

## How the block works

Enforcement is Windows Firewall, driven through `netsh`. Each blocked executable gets a pair of
rules — one outbound, one inbound — sharing a single name so one delete removes both. Rules are
named `IpLeash Block - <file name> [<8 hex>]`, where the hex is a hash of the full normalized path:
the file name keeps them readable in `wf.msc`, and the hash keeps them unique, because a list can
hold two installs of the same executable name and an npm `claude.exe` and a native `claude.exe`
would otherwise collide on one rule.

Windows Firewall gives block rules precedence over allow rules, so this works even if the
application already has allow rules of its own.

**Executables are screened before a rule is written.** netsh happily accepts a rule pointing at a
`.cmd` launcher or a missing file, reports success, and blocks nothing. A silent no-op is the worst
outcome for a kill-switch, so every candidate must exist, end in `.exe`, and start with the `MZ`
DOS signature. Executables that go missing later are flagged in red on their row.

**Crash recovery.** Rules are removed on exit, but a crash cannot run cleanup code. Active block
paths are recorded in `%LOCALAPPDATA%\IpLeash\active-block.json`; on startup that file is read and
the corresponding rules deleted, so a killed process can never leave an app blocked forever.

## Known limitations

- **Established connections may survive.** Windows Firewall applies rules to new connections; a
  TCP session already open when the rule lands can persist. A guaranteed instant cut requires
  killing the process, which this app deliberately does not do.
- **The rule binds to an executable, not a process tree.** Blocking `claude.exe` stops its own
  traffic, but a `git.exe` or `node.exe` it spawns is a separate image and keeps its access.
- **Launcher scripts cannot be blocked.** If your target is `python script.py` or a `.bat`, you
  must point at the `.exe` it starts — which then blocks *every* use of that interpreter.
- **Fail-closed can produce a spurious block** if all three public-IP providers are unreachable at
  once. Three independent providers make this rare, and every occurrence is logged.
- **Some processes cannot be picked.** Protected and system processes do not expose their image
  path; they appear greyed out in the picker, since a rule needs a path.
- **With a proxy in the path, the expected IP is the proxy's, not the machine's.** IpLeash measures
  its own egress. If a monitored application routes differently, the address being compared is not
  the one that application exits from.
