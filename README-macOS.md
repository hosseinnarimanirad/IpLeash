# IpLeash for macOS

The same kill-switch as the Windows app: a list of applications is cut off from the network
whenever this Mac's public IP (or its country) is not the expected one. It covers the VPN
tunnel dropping and the address falling back to the ISP's.

## Build and run

Requirements: macOS 12+, the .NET 10 SDK. Nothing extra is needed at runtime, because the
app ships self-contained.

```bash
./build-macos.sh            # Apple silicon → artifacts/macos/osx-arm64/IpLeash.app (+ .dmg)
./build-macos.sh osx-x64    # Intel
```

For development: `dotnet run --project src/IpLeash.Mac` (or open `IpLeash.Mac.slnx`).

The bundle is ad-hoc signed, so it runs on the Mac that built it. To hand it to someone else,
sign it with a Developer ID and notarize it. Otherwise Gatekeeper will ask them to approve it
in System Settings → Privacy & Security.

## How the code is shared

`src/IpLeash.Mac` is an Avalonia app that **links** the Windows project's models, view models,
`MonitorEngine`, geolocation, public-IP and settings code instead of copying them. The
fail-closed decision logic is one codebase. Only the platform services are separate
(`src/IpLeash.Mac/Platform`):

| Windows                   | macOS                                                  |
|---------------------------|--------------------------------------------------------|
| `FirewallService` (netsh) | `ProcessFreezeService` (SIGSTOP / SIGCONT)             |
| `ProcessWatcher`          | `MacProcessWatcher` (libproc)                          |
| `ProxyService` (WinINET)  | `MacProxyService` (`scutil --proxy` + CFNetwork)       |
| `LocalIpService`          | `MacLocalIpService` (Wi-Fi/Ethernet names, utun tunnels)|
| `AppDiscoveryService`     | `MacAppDiscoveryService`                               |
| `ExecutableFile` (MZ)     | `ExecutableFile` (.app bundle or Mach-O)               |
| Named mutex + event       | `SingleInstance` (flock + Unix socket)                 |
| Tray icon                 | Menu-bar icon                                          |

The shared files carry a few `#if IPLEASH_MACOS` blocks, only for log and dialog wording.
The Windows build is unchanged.

## How the block works (and why it differs from Windows)

macOS has no firewall that can block one application's outbound traffic. `pf` matches
addresses, ports and users, never processes. The Application Firewall only governs inbound
connections. Per-app filtering exists only as a Network Extension content filter, which
needs an Apple-issued entitlement and cannot be built or run from source.

So IpLeash **freezes** a blocked app's processes instead (`SIGSTOP`), and resumes them when
the IP matches again (`SIGCONT`). A stopped process cannot read or write a socket.

- **A target is an `.app` bundle or a Mach-O executable.** A bundle covers every process inside
  it, so all of Chrome's or Claude's helper processes are covered by one entry.
- **Children are frozen too.** The `git`, `node` and MCP-server processes a CLI spawns stop with
  it. The Windows rules never reach child processes.
- **Open connections stop as well.** That is stronger than Windows, where an established TCP
  session can survive a new rule.
- **New launches are caught by a sweep** that runs every 200 ms while anything is blocked.
- **Fail closed, crash-safe.** Frozen PIDs (with start times, so a recycled PID is never touched)
  and blocked targets are recorded in `~/Library/Application Support/IpLeash/`. The next launch
  resumes anything a crash left frozen.
- **No admin rights needed** for your own apps.
- **System locations are refused** (`/System`, `/usr` except `/usr/local`, `/bin`, `/sbin`).
  Freezing WindowServer would lock the whole session. IpLeash also refuses to target itself.

## Known limitations

- **A blocked app is unresponsive.** macOS shows the spinning cursor and may say it is not
  responding. That is the block working. Force Quit still works.
- **A copy launched mid-block runs for up to ~200 ms** before the sweep freezes it.
- **Data already sitting in a socket's kernel send buffer** can still drain after the freeze.
  Nothing new is sent.
- **Processes owned by another user** (for example `sudo …`) cannot be frozen. A direct match
  is reported as FAILED TO BLOCK. A child of a blocked app that runs as another user is skipped.
- **Scripts cannot be targets.** A `#!/usr/bin/env node` launcher runs inside node, so you must
  target node itself. Claude Code's native install (`~/.local/bin/claude`) is a real binary and
  works. The symlink is re-resolved every time, so updates keep matching.
- **Launching with every display asleep fails.** Avalonia's renderer needs an active display.
  Launch it after login, not on a lid-closed Mac.
- **With a proxy in the path, the expected IP is the proxy's**, exactly as on Windows.
