# IpLeash

A WPF desktop app that watches a list of Windows applications, reports the machine's current IP,
and cuts those applications off from the network whenever the public IP is not the one you expect.

The intended use is a VPN kill-switch: if the tunnel drops and your WAN IP reverts to your ISP
address, every monitored app stops talking to the internet until the expected IP is back.

## Behaviour

| Aspect | Behaviour |
|---|---|
| IPs shown | Public/WAN IP **and** every local adapter IPv4 (tunnel adapters flagged) |
| Public IP at launch | Looked up as soon as the window is shown, asynchronously — never blocks the UI |
| System proxy | Detected and displayed, with a warning when it sits in front of the IP lookup |
| IP that drives the decision | Public/WAN IP only — **one global expected IP** for the whole list |
| Expected IP | Persisted, plus a list of saved addresses offered as one-click chips |
| What gets blocked | Every enabled app, together |
| Enforcement | Windows Firewall block rules, **outbound and inbound**, one pair per executable |
| Public IP unreachable | **Fail closed** — treated as a mismatch, blocks are applied |
| Reaction time | Immediate on any network address change; otherwise every *N* seconds (default 15) |
| Closing the window | Hides to the notification area; enforcement continues |
| On exit (tray menu) | All block rules are removed, after confirming what that un-blocks |
| On next start | Rules left by a run that crashed are removed before anything else happens |
| Instances | Exactly one at a time; a second launch surfaces the first and exits |

## Requirements

- Windows 10/11
- .NET 9 desktop runtime (or the SDK)
- **Administrator rights** — creating firewall rules requires them, so the app is manifested
  `requireAdministrator` and prompts for UAC at launch.

## Build and run

From the repository root:

```powershell
dotnet build
.\src\IpLeash\bin\Debug\net9.0-windows\IpLeash.exe
```

## Window, tray and exit

**Closing the window does not quit.** It hides to the notification area and monitoring carries on;
a balloon tip says so the first time. This is a correctness fix as much as a convenience: closing
the window used to be the teardown path, so closing it while monitoring silently removed every
block rule and handed network access back to the apps being protected.

**Exit lives only in the tray menu**, and it is the single path that ends enforcement. When
something is actually blocked it names what is about to be released — *"Exiting will remove the
block rules for 2 app(s) (3 executable(s)) and restore their network access: Claude Code, curl"* —
because that is the one consequential, easy-to-do-by-accident action in the app.

The tray icon is the app's status at a glance, which is most of its value for a kill-switch:

| Icon | Meaning |
|---|---|
| Grey | Not monitoring |
| Green | Public IP matches; nothing blocked |
| Red | Blocked |
| Amber | Public IP unknown; blocking fail-closed |

Hovering shows the same state in words, plus how many apps are cut off.

> On Windows 11 a newly registered tray icon starts in the **overflow flyout** behind the `^`
> chevron, not pinned to the taskbar. Drag it out, or use Settings → Personalization → Taskbar →
> Other system tray icons to pin it.

**Only one instance ever runs.** A global mutex enforces it, and a second launch signals the
running instance to un-hide itself and then exits immediately — so re-running the exe is a
perfectly good way to reopen the window from the tray. Single-instance is not a nicety here: a
second instance's startup cleanup would delete the first's *active* rules, silently unblocking
apps while the first window still displayed BLOCKED.

Tray support uses `System.Windows.Forms.NotifyIcon` — WPF has no tray primitive, and this ships
in the Windows Desktop SDK, so it needs no third-party package. Enabling WinForms puts
`System.Windows.Forms` and `System.Drawing` into the implicit usings, where they collide with
`System.Windows` and `System.Windows.Media`; both are removed in the csproj rather than
disambiguated at every use site, since only `TrayIconService` needs them.

## The public IP reading

The lookup runs at launch, on every network address change, and on **Check now** — before
monitoring starts as well as during it. While stopped it is a *probe*: it refreshes the display
and never creates, removes, or even consults a firewall rule. That distinction matters for
honesty of the UI — a failed lookup while stopped reads as `unavailable`, not as the amber
fail-closed banner, because nothing is being enforced.

Nothing blocks the UI thread. The probe is awaited only after the window is shown and painted,
so the message loop keeps running for the whole lookup; the field shows `checking…` until an
answer arrives. On this machine a full lookup takes roughly 0.6 s.

### Expected IP

The address in use is saved and restored between runs. Beyond that, **Save** adds it to a list of
remembered addresses shown as chips below the field — click one to switch to it, `✕` to forget
it. **Use detected** captures whatever the machine's public IP is right now, which is the quick
way to pin the expected value once you are on the VPN you want.

### System proxy

Read from three places: the WinINET per-user settings (`ProxyEnable` / `ProxyServer` /
`AutoConfigURL`), the `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY` environment
variables, and — the authoritative one — what .NET actually resolves for the IP-probe URL.

That last check is the point of the feature. **A proxy in front of the probe changes what the
public IP means:** the address reported back is the proxy's exit address, not this machine's own.
A proxy can also be configured but bypassed for the probe host, in which case the reading is
still genuinely yours — which is why the panel distinguishes "configured" from "affects this
measurement", and only warns in the second case.

## The monitored list

An entry is a **name plus one or more executables**, blocked and unblocked as a unit. That
grouping is what lets "Claude" be one row even when it is installed twice (an npm build and a
native build) and running as four processes.

Each entry shows:

- an enable checkbox — disabled entries stay in the list but are never blocked
- a live block badge: `allowed` / `BLOCKED` / `PARTIALLY BLOCKED (1/2)`
- a summary line, e.g. *4 processes across 1 executable*
- one row per executable with its directory and its **live PIDs**

Three ways to add:

| Button | What it does |
|---|---|
| **Detect Claude…** | Finds Claude Code and Claude Desktop wherever they are installed on *this* machine |
| **From running process…** | Lists every running executable with its process count; tick one or more |
| **Browse…** | Pick an `.exe` by hand |

The list is editable only while stopped. Changing targets under a running engine would mean
reconciling rules mid-flight for no real gain.

### Why auto-detection exists

Claude's install path differs between systems — npm global prefix, a native install under the
profile, a per-user desktop install — so a hand-typed path does not survive being moved to
another machine. Detection re-resolves it. It looks in, in order of confidence:

**Claude Code** — running processes (most reliable: reports the real image path whatever the
install method), `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`,
`~\.local\bin`, `~\.claude\local`, `%ProgramFiles%\nodejs\...`, and every directory on `PATH`
(both `claude.exe` directly and the npm package next to a shim).

**Claude Desktop** — running processes, `%LOCALAPPDATA%\AnthropicClaude` including versioned
`app-*` folders, `%LOCALAPPDATA%\Programs\Claude`, and `%ProgramFiles%\Claude`.

Results are deduplicated by full path and screened (see below). Re-running detection merges new
installs into the existing entry instead of creating a duplicate row.

## How the block works

Two rules per executable, sharing one name:

```
netsh advfirewall firewall add rule name="<RULE>" dir=out action=block program="<exe>" enable=yes profile=any
netsh advfirewall firewall add rule name="<RULE>" dir=in  action=block program="<exe>" enable=yes profile=any
```

`<RULE>` is `IpLeash Block - <file name> [<8 hex>]`, where the hex is a hash of the full,
normalized path. The file name keeps it readable in `wf.msc`; the hash keeps it unique, which
matters because a list can hold two different installs of the same executable name — an npm
`claude.exe` and a native `claude.exe` would otherwise collide on a single rule.

Windows Firewall gives block rules precedence over allow rules, so this works even if the
application already has allow rules.

netsh output is localized, so success is decided purely by exit code — the app never parses the
text. `show rule` exits 0 when the rule exists and 1 when it does not.

### Executable screening

netsh happily accepts a program rule pointing at a `.cmd` launcher or a missing file, reports
success, and blocks nothing. A silent no-op is the worst possible outcome for a kill-switch, so
every candidate must exist, end in `.exe`, and start with the `MZ` DOS signature. Executables
that go missing later are flagged in red on their row.

### Crash recovery

Rules are removed on exit, but a crash cannot run cleanup code. The paths of all active blocks
are recorded in `%LOCALAPPDATA%\IpLeash\active-block.json`; on startup that file is read and
the corresponding rules deleted, so a killed process can never leave an app blocked forever.

This is also why only one instance may run: a second instance's startup cleanup would delete the
first instance's *active* rules, silently unblocking apps while the first window still read
BLOCKED.

## Known limitations

- **Established connections may survive.** Windows Firewall applies rules to new connections; a
  TCP session already open when the rule lands can persist. A guaranteed instant cut requires
  killing the process, which this app deliberately does not do. `MonitorEngine.ApplyOneAsync` is
  where that would go.
- **The rule binds to an executable, not a process tree.** Blocking `claude.exe` stops its own
  traffic, but a `git.exe` or `node.exe` it spawns is a separate image and keeps its access.
- **Launcher scripts cannot be blocked.** If your target is `python script.py` or a `.bat`, you
  must point at the `.exe` it starts — which then blocks *every* use of that interpreter.
- **Fail-closed can produce a spurious block** if all three public-IP providers are unreachable
  at once. Three independent providers make this rare, and every occurrence is logged.
- **Some processes cannot be picked.** Protected and system processes do not expose their image
  path; they appear in the picker greyed out, since a rule needs a path.
- **With a proxy in the path, the expected IP is the proxy's, not the machine's.** IpLeash
  measures its own egress. If a monitored application routes differently — its own proxy setting,
  or none — then the address being compared is not the one that application exits from. The proxy
  panel exists to make this visible rather than to resolve it.

## Architecture

Strict MVVM, .NET 9, two Microsoft packages (`CommunityToolkit.Mvvm`,
`Microsoft.Extensions.DependencyInjection`).

```
src/IpLeash/
  App.xaml.cs            composition root; single-instance mutex, startup cleanup, idempotent teardown
  Models/                MonitoredApp, AppSettings, MonitorSnapshot, MonitoredAppState,
                         RunningExecutable, AdapterInfo, LogEntry, DiscoveredApp
  Services/              one interface + one implementation each
    MonitorEngine.cs       the state machine: poll -> decide -> reconcile every enabled executable,
                           plus the display-only probe path used while stopped
    FirewallService.cs     netsh add/delete/show, exit-code driven, path-scoped rule names
    AppDiscoveryService.cs Claude Code / Claude Desktop location probing
    ProcessWatcher.cs      per-path PID matching + running-executable enumeration
    PublicIpService.cs     three providers, 5 s timeout each
    ProxyService.cs        WinINET registry + env vars + effective proxy for the probe URL
    LocalIpService.cs      adapter enumeration
    ExecutableFile.cs      .exe + MZ screening
    SettingsStore.cs       JSON settings load/save, tolerant of a corrupt file
    BlockStateStore.cs     crash-recovery record
  ViewModels/            MainViewModel, MonitoredAppViewModel, ExecutableViewModel,
                         ProcessPickerViewModel
  Assets/                icon set: brand mark plus one per tray state
  Views/                 MainWindow, ProcessPickerWindow (+ empty code-behinds),
                         DialogCloser, Converters/,
                         Services/DialogService, Services/TrayIconService
    Styles/Theme.xaml    design tokens and control templates, merged in App.xaml
```

The window's `Closing` event is subscribed in `App`, not in the window's code-behind — which
stays empty. Hiding versus quitting is a lifetime decision, and lifetime belongs to the
composition root.

### Icons

`Assets/*.ico` are generated, not hand-drawn: one shield-on-rounded-square mark rendered in five
colours at 16/24/32/48/64 px. Frames are classic 32bpp DIBs rather than PNG-in-ICO — PNG frames
are valid and smaller, but GDI+ cannot rasterise them, and `NotifyIcon` takes a
`System.Drawing.Icon`, so a PNG-framed file loads without error and then silently fails to
produce a tray image.

### Visual design

`Views/Styles/Theme.xaml` holds every colour, radius, font and control template. Views compose
from named styles and never spell out a colour, so the palette is changed in one place.

Buttons and text inputs are **templated** rather than merely restyled — the stock WPF chrome has
a gradient and a 3 px corner that no combination of property setters removes. Three button
weights (`PrimaryButton`, `SecondaryButton`, `GhostButton`) plus `IconButton` and `ChipButton`
cover the app, each with hover and pressed states. Text fields get a focus ring in the accent
colour, and a validation error overrides focus styling so an invalid focused field still reads
as invalid.

The checkbox keeps its default template: its state visuals are subtle and hard to improve on,
and replacing it would risk the indeterminate and keyboard-focus states for no visual gain.

Every inline control shares one `ControlHeight` token. Buttons and text fields otherwise size
themselves from their own padding and never quite line up when placed on the same row.

**Lists are an explicit `ScrollViewer` plus an `ItemsControl`, never a `ListBox`.** A ListBox
measures its items against infinite width, which silently disables both `TextWrapping` and
star-sized columns — long text then runs past the edge and is clipped mid-word rather than
wrapping or ellipsizing. That is a real hazard for the activity log, whose most important
messages (`FAILED TO BLOCK …`) are also its longest. Neither list needs selection — the picker
tracks its own through checkboxes — so the container that causes the problem is simply not used,
and the `ListRow` style supplies the row chrome it would have provided.

The rules the code is held to:

- Both windows' code-behind contain `InitializeComponent()` and nothing else; the XAML has no
  event handlers. Dialogs close via the `DialogCloser` attached property, not a click handler.
- ViewModels reference no WPF type — not even `ICollectionView`, which is why the process
  picker filters by rebuilding its collection rather than using a collection view.
- Modal UI goes through `IDialogService`; validation is `INotifyDataErrorInfo` via
  `ObservableValidator`.
- `MonitorEngine` has no UI awareness. It uses `System.Timers.Timer`, not `DispatcherTimer`, so
  it is never pinned to the UI thread; the ViewModel marshals via a captured
  `SynchronizationContext`.
- Every service sits behind an interface, so the engine can be tested without a firewall or a
  network.
- Evaluation is serialized by a `SemaphoreSlim`, because the poll timer and the
  `NetworkAddressChanged` event can otherwise fire together and issue conflicting netsh calls.
