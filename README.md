# DeskWeave

Native Windows desktop widgets inspired by the widgets in [kasuken/omarchy-setup](https://github.com/kasuken/omarchy-setup).
DeskWeave is built with .NET 10 and WPF. It pins borderless, click-through panels to the desktop and styles them with the Neon Intrusion palette.

## Widgets (MVP)

| Widget | Position | Shows |
| --- | --- | --- |
| **World clock** | Bottom center | Your local city as a large centered clock, plus 16 cities sorted by UTC offset, with dates, offsets and `+1d`/`-1d` day markers |
| **System monitor** | Top right | CPU (with a 90 s sparkline), RAM, system drive space and I/O, temperatures, network adapter, IP, Wi-Fi signal, and up/down rates |
| **GitHub status** | Below the monitor | Overall status, active incidents, components, and scheduled maintenance from the public [githubstatus.com](https://www.githubstatus.com) API |
| **AI subscriptions** | Top left | Plan, usage meters, consumption and reset times for paid Claude, Codex, GitHub Copilot and Antigravity subscriptions |

The GitHub status widget polls at most once per minute and honors `Retry-After`.
It keeps a 24-hour cache in `%LOCALAPPDATA%\DeskWeave` and shows stale data in gray while offline.

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

## Known limitations

- **Temperatures:** CPU temperature uses the hottest ACPI thermal zone, which may not match the CPU package sensor. SSD temperature needs administrator rights, so it shows `N/A`.
- **Show Desktop:** Win+D (Show Desktop) hides the widgets until they are toggled again.
- **Monitors:** widgets appear on at most two screens: the primary and one secondary.
- **Interaction:** the widgets are click-through, so they have no tooltips or other interaction.
- **AI quotas:** DeskWeave doesn't refresh expired sign-ins. Running the provider's CLI renews them.
