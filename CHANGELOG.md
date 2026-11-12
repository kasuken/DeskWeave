# Changelog

All notable changes to DeskWeave are documented here using [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and semantic versioning.

## [0.4.0] - 2026-11-12

### Added

- **Agent attention** - Opt into Claude Code lifecycle hooks to see independent coding turns, approval/input requests and recent outcomes, with explicit disconnected/unknown states and safe workspace navigation from the tray.

## [0.3.0] - 2026-11-11

### Added

- **Estimated quota runway** - See whether recent subscription usage is likely to last until reset, with clear indication when there is not enough fresh history for an estimate.

## [0.2.0] - 2026-11-11

### Added

- **Service health** - Show public Claude and OpenAI health alongside GitHub and Copilot, with a grouped provider overview, independently selectable providers, official status links and clearly marked offline readings.
- **Per-process resource rankings** - Show the top three processes independently by CPU, working-set memory and GPU utilization in a dedicated widget window, with color-coded sections, ranked rows and relative usage bars. Keep its width aligned with the System panel when scaling to fit the screen.

## [0.1.0] - 2026-11-06

### Added

- **AI subscription headroom** - Display recognized paid Claude, Codex, GitHub Copilot and Antigravity plans, supported usage limits and reset times using existing provider sign-ins.
- **Local machine health** - View CPU history, RAM, system-drive space and I/O, network rates, and available GPU/NPU engine, memory and optional sensor readings. Unsupported accelerator readings stay hidden; measured idle zeros remain visible.
- **Balanced world clocks** - Keep local time centered between eight cities on each side, with UTC offsets and day-change markers.
- **GitHub service status** - See public service health, active incidents, components and maintenance, with explicitly marked last-known readings while offline.
- **Per-screen widgets** - Choose primary, secondary or both displays from the tray, with saved preferences, per-monitor DPI/layout and a primary fallback when the selected secondary disconnects.
- **Portable Windows build** - Run the self-contained Windows x64 ZIP without installing the .NET SDK or runtime.
