# DeskWeave

Native Windows desktop widgets inspired by the widgets in [kasuken/omarchy-setup](https://github.com/kasuken/omarchy-setup).
DeskWeave is built with .NET 10 and WPF. It pins borderless, click-through panels to the desktop and styles them with the Neon Intrusion palette.

## Widgets (MVP)

| Widget | Position | Shows |
| --- | --- | --- |
| **World clock** | Bottom center | Your local city as a large centered clock, with exactly 8 cities on each side sorted by UTC offset, dates, offsets and `+1d`/`-1d` day markers |
| **System monitor** | Top right | CPU (with a 90 s sparkline), RAM, system drive space and I/O, temperatures, network adapter, IP, Wi-Fi signal, up/down rates, and available GPU/NPU telemetry |
| **GitHub status** | Below the monitor | Overall status, active incidents, components, and scheduled maintenance from the public [githubstatus.com](https://www.githubstatus.com) API |
| **AI subscriptions** | Top left | Plan, usage meters, consumption and reset times for paid Claude, Codex, GitHub Copilot and Antigravity subscriptions |

The world clock selects from Honolulu, Anchorage, San Francisco, Denver, Chicago, New York, Sao Paulo, London, Zurich, Cairo, Moscow, Dubai, Mumbai, Singapore, Tokyo, Sydney and Auckland, each in a distinct time zone.
The local city moves to the center when its zone matches; otherwise, the nearest-offset city is omitted so both wings still contain 8 clocks. Equal offsets use the listed order to break ties. Cities are split evenly by count, not by whether they are ahead of or behind local time.

The GitHub status widget polls at most once per minute and honors `Retry-After`.
It keeps a 24-hour cache in `%LOCALAPPDATA%\DeskWeave` and shows stale data in gray while offline.

### GPU and NPU telemetry

The existing two-second background sampler reads Windows GPU Engine and GPU Adapter Memory counters.
DXCore discovers hardware adapters and maps counters by their full LUID, supporting multiple GPUs without matching by name or array order.
Adapters explicitly identified by Windows as NPUs are labeled NPU; other compute-only devices are labeled Accelerator.

- **Utilization:** overall load is the busiest physical engine, not the sum of overlapping engines. Process contributions are added only within the same engine, capped at 100%. Engine types (3D, compute, video encode/decode, copy, etc.) appear exactly when the driver exposes them. New/reset process counters make their engine and overall load unknown for one sample, rather than looking idle while warming up.
- **Memory:** dedicated usage and adapter-memory capacity are separate from shared system-memory usage and its maximum. Shared capacity is a limit, not reserved VRAM. Integrated GPUs may report zero dedicated adapter memory; their system-memory reservation must not be presented as discrete VRAM. Unknown capacities are omitted.
- **Sensors:** read-only WDDM adapter-performance queries supply temperature, relative power percentage (not watts), and fan RPM where the driver supports them. Zero sensor placeholders are hidden; stopped fans are shown as 0 RPM only when fan capability is confirmed.
- **Unavailable:** missing, inaccessible, or failed new metrics are omitted. An adapter with only identity/static capacity and no meaningful live readings is hidden. Measured zero utilization or memory use remains visible. Existing CPU/RAM/disk/network unavailable behavior is unchanged.
- **Permissions and overhead:** no administrator requirement, vendor monitoring dependency, kernel-driver installation, or configuration changes. Discovery refreshes every 30 seconds; each counter category is read once per sample. Counter failures are traced and retried, with no stale readings substituted for failures.

NPU identity does not guarantee utilization support: Windows/driver versions differ, and generic ML support alone does not prove a device is an NPU.
Sensors are optional and there is no universal Windows GPU/NPU power-in-watts or fan API.
Sensor queries address physical node 0 on linked adapters.
Hardware-monitoring packages were deliberately avoided because their extra vendor libraries, privileged driver paths and licensing surface would not guarantee these sensors.

### AI subscriptions

The widget refreshes every 5 minutes and reuses the sign-ins of the official tools. It shows only recognized paid plans.

| Provider | Sign-in it reads | Endpoint |
| --- | --- | --- |
| Claude | `%CLAUDE_CONFIG_DIR%` or `~\.claude\.credentials.json` | `api.anthropic.com/api/oauth/usage` |
| Codex | `%CODEX_HOME%` or `~\.codex\auth.json` (ChatGPT login) | `chatgpt.com/backend-api/wham/usage` |
| GitHub Copilot | the `gh` CLI login (DeskWeave runs `gh api /copilot_internal/user` and never sees the token) | via `gh` |
| Antigravity | Credential Manager entry `gemini:antigravity`, or `~\.gemini\antigravity-cli\antigravity-oauth-token` | `daily-cloudcode-pa.googleapis.com` |

- **Missing or free:** a provider without a sign-in, or on a free plan, is not shown. The widget hides when no paid plans are found.
- **Expired sign-in:** the plan stays visible with "Sign in to refresh". Sign in again with that provider's CLI.
- **Offline:** the last known values are kept for up to 24 hours, gray and marked "Last known".
- **Privacy:** tokens are read on each refresh and sent only to the provider's own HTTPS host. Redirects are not followed, and tokens are never logged or cached. `%LOCALAPPDATA%\DeskWeave\ai-quota.json` holds only plans, limits and reset times.

## Responsive layout

- Content scales with each target monitor's work area: `min(width/1920, height/1080)`, clamped to 0.7–1.8. The layout is Per-Monitor-V2 DPI aware, so every screen uses its own DPI.
- The world clock picks one of three layouts depending on the available width:
  - a single strip;
  - balanced, wrapped wings;
  - a stacked layout on narrow screens.
- GitHub status moves to the left of the monitor when there isn't enough vertical space, and hides when there's no room at all.
- The system monitor scales down further when necessary to fit all available accelerator cards above the clock; click-through panels do not require scrolling.
- AI subscriptions hides when it doesn't fit above the clock or next to the monitor.
- The layout is recalculated automatically when monitors are connected or disconnected, or when the resolution, DPI, taskbar or work area changes.

## Screens

The tray menu chooses where the widgets appear:

- **Primary screen only** (default)
- **Secondary screen only**
- **Both screens**: each screen gets its own widgets laid out for that screen. Data is sampled and fetched once and shared.

The choice is saved in `%LOCALAPPDATA%\DeskWeave\settings.json`. With one screen, **Secondary screen only** is disabled. If the saved secondary screen is disconnected, the widgets move to the primary screen until it returns, and the saved choice is kept. With more than two monitors, the secondary screen is the leftmost non-primary monitor (then topmost, then by device name), and **Both** uses only the primary and that secondary.

## Build and run

```powershell
dotnet build DeskWeave.slnx
dotnet test
dotnet run --project src\DeskWeave
```

DeskWeave runs from the notification area. Its tray menu has these items:

- **Show widgets**
- **Refresh layout**
- **Start with Windows** (adds a per-user Run key entry)
- **Primary screen only**, **Secondary screen only**, **Both screens** (see [Screens](#screens))
- **Exit**

Double-click the tray icon to show or hide the widgets.

## Project layout

- `src\DeskWeave.Core`: platform-independent logic:
  - world clock snapshots;
  - monitor selection and the saved screen preference;
  - GitHub status parsing, polling and caching;
  - AI subscription parsing, refresh and caching;
  - rates, formatting and metric history.
- `src\DeskWeave`: WPF app:
  - desktop windows and layout;
  - the Windows metrics sampler;
  - the widgets;
  - the tray icon.
- `tests\DeskWeave.Core.Tests`: xUnit tests for the Core library.
- `tests\DeskWeave.Tests`: STA WPF tests for responsive clock layout and integrated widget rendering.

## Known limitations

- **Temperatures:** CPU temperature uses the hottest ACPI thermal zone, which may not match the CPU package sensor. SSD temperature needs administrator rights, so it shows `N/A`.
- **Show Desktop:** Win+D (Show Desktop) hides the widgets until they are toggled again.
- **Monitors:** widgets appear on at most two screens: the primary and one secondary.
- **Interaction:** the widgets are click-through, so they have no tooltips or other interaction.
- **AI quotas:** DeskWeave doesn't refresh expired sign-ins. Running the provider's CLI renews them.
