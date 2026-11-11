# DeskWeave

**Your desktop, tuned for AI-native engineering.**

AI subscription quotas. Machine health. GitHub status. World time. Native Windows widgets that keep your working context in view, without another dashboard tab.

![DeskWeave on an actual Full HD secondary monitor: AI subscription quotas at top left, CPU, GPU and NPU metrics and GitHub status at right, and 17 clocks along the bottom.](docs/assets/deskweave-full-hd.png)

*Actual secondary-screen capture at native 1920 x 1080. The network address line is redacted; all other readings, including the sign-in warning, are unchanged.*

## Less tab-switching. More context.

- **See your AI headroom.** Recognized paid Claude, Codex, GitHub Copilot and Antigravity plans, with available usage meters and reset times.
- **Know what your machine is doing.** CPU history, RAM, top processes by CPU/memory/GPU, system-drive space and I/O, network rates, and GPU/NPU telemetry where Windows exposes it.
- **Check GitHub before debugging your setup.** Public service health, affected components, active incidents and scheduled maintenance.
- **Work across time zones.** Your local clock takes center stage, with eight cities on each side, UTC offsets and day-change markers.
- **Put a spare screen to work.** Choose primary, secondary or both. Borderless, click-through panels stay above the wallpaper and behind your apps.

Built with **.NET 10 + WPF**, styled with the Neon Intrusion palette, and inspired by [omarchy-setup](https://github.com/kasuken/omarchy-setup).

## Up and running

### Portable Windows download

Download the self-contained Windows x64 build from [GitHub Releases](https://github.com/kasuken/DeskWeave/releases/latest):

1. Download [DeskWeave-0.1.0-win-x64.zip](https://github.com/kasuken/DeskWeave/releases/download/v0.1.0/DeskWeave-0.1.0-win-x64.zip) and [SHA256SUMS.txt](https://github.com/kasuken/DeskWeave/releases/download/v0.1.0/SHA256SUMS.txt).
2. Compare `Get-FileHash .\DeskWeave-0.1.0-win-x64.zip -Algorithm SHA256` with the checksum file.
3. Extract the **entire** ZIP to a stable, writable folder, then run `DeskWeave.exe` inside its folder. Keep the bundled files together.

Requires a Windows 10/11 x64 desktop supported by .NET 10. The portable ZIP includes the runtime; no SDK or runtime installation and no administrator access are needed. Linux and macOS cannot run the WPF app. Provider CLIs and paid accounts are optional; the other widgets work without them.

> [!IMPORTANT]
> The build is unsigned. Windows SmartScreen or antivirus software may warn about an unrecognized app. Only win-x64 is validated; no ARM64 build is provided.

### Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then use PowerShell:

```powershell
git clone https://github.com/kasuken/DeskWeave.git
Set-Location .\DeskWeave
dotnet run --project .\src\DeskWeave\DeskWeave.csproj --configuration Release
```

The command restores dependencies, builds the app and starts its notification-area icon. No administrator access is required.

> [!TIP]
> No app window to open: look for **DeskWeave** in the notification area, including its hidden-icons menu. Only one instance runs at a time; exit an existing instance before launching a different build.

## Make it your desktop

Right-click the tray icon to select a screen:

| Tray option | Behavior |
| --- | --- |
| **Primary screen only** | Default. Widgets appear on the primary display. |
| **Secondary screen only** | Widgets appear on one non-primary display. Disabled when none is connected. |
| **Both screens** | A separately laid-out set on each display, sharing one set of sampling and polling loops. |

Screen selection survives restarts. Disconnecting the selected secondary temporarily moves its widgets to the primary; reconnecting restores the saved choice. With more than two monitors, DeskWeave uses the primary and the leftmost non-primary display, breaking ties by top edge, then device name.

Double-click the icon to show/hide widgets. The menu also provides **Show widgets**, **Refresh layout**, **Start with Windows** and **Exit**. Startup registration writes only a per-user Windows Run entry pointing to the current executable; keep that build in a stable location if you enable it.

Each screen uses its own DPI and work area. Layout updates when resolution, scaling, taskbar or connected displays change. Clocks wrap or stack on narrower screens; the system panel scales down to fit. GitHub status may move beside it, and status or quota panels hide when space is insufficient.

## Connect your AI subscriptions

There is no separate DeskWeave account or API-key form. Sign in with the supported provider tools; DeskWeave reads their existing local credentials on each refresh.

| Provider | Sign-in source | Requests go to |
| --- | --- | --- |
| **Claude** | `~\.claude\.credentials.json`, or `.credentials.json` under `CLAUDE_CONFIG_DIR` | `api.anthropic.com/api/oauth/usage` |
| **Codex** | ChatGPT sign-in in `~\.codex\auth.json`, or `auth.json` under `CODEX_HOME`; API-key mode is not supported | `chatgpt.com/backend-api/wham/usage` |
| **GitHub Copilot** | GitHub CLI (`gh`) installed on `PATH` and signed into `github.com` | `gh api --hostname github.com /copilot_internal/user`; `gh` handles the token |
| **Antigravity** | Windows Credential Manager entry `gemini:antigravity`, falling back to `~\.gemini\antigravity-cli\antigravity-oauth-token`; `AGY_DIR` overrides that directory | `daily-cloudcode-pa.googleapis.com` |

Here, `~` means your Windows user-profile folder. These environment variables override credential directories, not individual file paths.

Quota refreshes run every **five minutes**. Missing credentials and free or unrecognized plans are omitted; no recognized plans means no quota panel. An available plan can still report **Quota unavailable** if its endpoint returns no supported limits.

> [!IMPORTANT]
> DeskWeave does not renew credentials. For **Sign in to refresh**, renew the sign-in using that provider's tool and wait for the next refresh. Provider usage APIs, including internal endpoints, can change independently of DeskWeave. This is a quota display, not a billing meter or agent-task tracker.

## Data, privacy and offline behavior

Machine metrics and clocks are calculated locally. **DeskWeave is not offline-only:** it contacts the public [GitHub Status API](https://www.githubstatus.com/api/v2/summary.json) at most once per minute, honoring `Retry-After`, and contacts the quota endpoints above when credentials are available.

Direct quota requests send bearer tokens only to the allowed provider HTTPS hosts; redirects are disabled. Those tokens are not logged or cached. For Copilot, DeskWeave invokes `gh` and reads its response rather than retrieving the GitHub token.

Local files live in `%LOCALAPPDATA%\DeskWeave`:

| File | Contents |
| --- | --- |
| `settings.json` | Saved monitor mode: `Primary`, `Secondary` or `Both`. Prefer changing it through the tray. |
| `github-status.json` | Cached public service status. |
| `ai-quota.json` | Display data: plan labels, usage limits, reset times and freshness/status metadata; no credentials. |

Status and quota caches retain last-known data for up to **24 hours**. Offline readings are explicitly marked and gray, not presented as fresh. Without recent data, status reports unavailable and quota providers can disappear.

> [!WARNING]
> The system panel displays your network name and IP address. Review screenshots before sharing them, and never publish provider credential files or raw authentication responses.

## Hardware and desktop limits

GPU/NPU readings depend on the Windows version, hardware and driver. No extra monitoring driver or vendor package is installed. Unsupported accelerator readings are **hidden**, not invented; an adapter with no meaningful live telemetry is hidden too.

<details>
<summary>What the hardware numbers mean</summary>

- Sampling runs every two seconds; adapter discovery refreshes every 30 seconds. DXCore identifies adapters, Windows performance counters supply load/memory, and read-only WDDM queries supply optional sensors.
- Overall utilization is the busiest physical engine, not the sum of overlapping engine percentages. New/reset counters can make a reading temporarily unknown.
- The system panel lists the top three processes independently by CPU, working-set memory and GPU. Process CPU is normalized to total logical-processor capacity; GPU usage follows the busiest engine. Windows does not expose meaningful per-thread memory or GPU attribution.
- Dedicated and shared memory are separate. Shared capacity is a limit, not reserved VRAM; integrated-GPU dedicated capacity is not necessarily discrete VRAM.
- NPU identity does not guarantee usable utilization counters. Only explicitly identified NPUs receive that label; other compute-only devices are labeled Accelerator.
- Temperature, fan RPM and **relative power percentage** appear only when supported. Relative power is not watts. Sensor queries use physical node 0 on linked adapters.
- CPU temperature is the hottest available ACPI thermal zone, not necessarily the CPU package. SSD temperature is currently unimplemented and remains `N/A`; running as administrator does not enable it.

</details>

Widgets are read-only and click-through: no dragging, scrolling, tooltips or in-panel controls. **Win+D** can hide them; use the tray toggle to show them again. At most two displays are targeted.

The city catalog and colors currently live in source: [WorldClock.cs](src/DeskWeave.Core/WorldClock.cs) and [NeonIntrusion.xaml](src/DeskWeave/Theme/NeonIntrusion.xaml). There is no theme or city editor.

## Development

From the repository root on Windows:

```powershell
dotnet build .\DeskWeave.slnx --configuration Release
dotnet test .\DeskWeave.slnx --configuration Release --no-build
```

| Location | Responsibility |
| --- | --- |
| [`src/DeskWeave.Core`](src/DeskWeave.Core) | Platform-independent clocks, display selection, quota/status parsers, polling, caching and metric formatting. |
| [`src/DeskWeave`](src/DeskWeave) | WPF widgets, per-monitor layout, Windows telemetry/interop and tray lifecycle. |
| [`tests/DeskWeave.Core.Tests`](tests/DeskWeave.Core.Tests) | xUnit coverage for core behavior. |
| [`tests/DeskWeave.Tests`](tests/DeskWeave.Tests) | STA WPF coverage for clock layouts and integrated widget rendering. |

The tray is the interactive surface; `WidgetHost` owns shared data loops and `WidgetSet` owns each screen's windows. Keep provider parsing in Core and Windows-specific work in the WPF project.

To reproduce the portable build from the [v0.1.0 source](https://github.com/kasuken/DeskWeave/tree/v0.1.0), use .NET SDK 10.0.403 on Windows x64:

```powershell
dotnet publish .\src\DeskWeave\DeskWeave.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --output .\artifacts\DeskWeave-0.1.0-win-x64
Compress-Archive -Path .\artifacts\DeskWeave-0.1.0-win-x64 -DestinationPath .\artifacts\DeskWeave-0.1.0-win-x64.zip
```

`--self-contained true` includes the .NET runtime in the publish folder. Package the entire folder in the release ZIP, not just `DeskWeave.exe`.

See the [changelog](CHANGELOG.md) for user-facing changes. No project license has been selected; public availability does not grant an open-source license.
