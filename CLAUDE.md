# CLAUDE.md — NOC Multi-Monitor Display App

This file gives Claude Code the standing context for this project. Read `noc-display-app-spec.md` in full before starting any work — it contains the complete product spec, architecture, and every decision made so far. This file summarizes the parts that most affect how code should be written.

## What this app is
A Windows desktop app for a single NOC/datacenter workstation. It drives 6 monitors connected through a fixed-grid HDMI video-wall splitter (not true independent multi-monitor output), pinning specific dashboards/apps to specific screen regions and keeping them running reliably, unattended, for long stretches.

## The one thing to never forget
**Windows only ever sees ONE virtual display.** The hub physically slices one combined video signal into a grid — it does not give the OS 6 independent monitors. This means:
- Do NOT use `System.Windows.Forms.Screen.AllScreens` or native multi-monitor APIs to detect "monitors" — there's only one, from Windows' point of view.
- The app itself is the compositor: one fullscreen borderless window, subdivided into cells that are rendered/positioned to match the hub's physical cut points exactly.
- Cell pixel boundaries must come from the hub's actual spec (input resolution + grid mode), never estimated or hardcoded from a guess.

## Tech stack
- **C# / .NET** (current LTS)
- **WPF** for the compositor window and editor UI
- **WebView2** for any browser/URL-based dashboard cell (this covers most cells — most dashboards here are web-based)
- **Win32 P/Invoke** (`SetParent`, related window APIs) for reparenting native (non-browser) app windows into a cell. Capture-based rendering (Windows Graphics Capture API) is NOT needed for v1 — all target dashboard apps are standard windowed apps with no hardware-accelerated rendering.

## Key architectural decisions (do not relitigate without checking with the user)
- Cells stay clickable/interactive — this is not a pure read-only display. No "lock" mechanism against accidental clicks.
- Went-dark and frozen-content detection share the same visual indicator (red cell border). The activity log, not the UI, is where the two are distinguished.
- Watchdog relaunch shows a visible "restarting" state on the cell — never a silent relaunch.
- App auto-launches on Windows startup via an auto-login kiosk session — do not build a "wait for manual login" path.
- Switching split presets (e.g. 2x3 → 2x2) preserves bindings for cells that become hidden, so switching back restores them. Do not discard bindings on preset switch.
- Activity log is a plain text/log file on disk — no in-app log viewer needed for v1. Every entry includes timestamp + cell.
- Manual profile switching only for v1 (editor + a dedicated quick-switch hotkey). Do not build scheduled auto-switching yet, but keep the profile-switching code structured so it can be added later without a rewrite.
- Global dim/blank and manual snapshot export are each their own separate dedicated hotkey — do not combine into one modifier+key scheme.
- No idle/burn-in protection — monitors are powered off after hours, so this isn't needed.
- No cross-cell spanning layouts in v1 — cells are fixed rectangles for now.
- No remote management, no multi-workstation sync, no external alerting integrations (Slack/PagerDuty/email), no scripting/plugin engine — these are explicitly out of scope for v1. Do not add them speculatively.

## Build order
Follow the phase order in the spec's "Procedural Roadmap" and "Timeline" sections: Foundation → Reliability → Content refresh → Polish. Do not jump ahead to Phase 3 features while Phase 1 is incomplete — reliability work in Phase 2 is explicitly called out as the highest-risk, least-compressible phase and depends on Phase 1 being solid first.

## Open items that block certain work
Some tasks can't be finished until the user provides hardware-specific facts (see spec's "Open Questions"): the hub's exact expected input resolution and split-point coordinates, whether it does bezel compensation, and whether it exposes any per-port health/status feed. Where code depends on these, use clearly marked placeholder constants (e.g. `TODO_HUB_SPEC`) rather than guessing real numbers, and flag it back to the user instead of silently assuming a value.

## Testing
The user has direct access to the real target hardware (the actual workstation, hub, and GPU) and will test there — don't assume a dev-only simulated environment is equivalent, especially for Phase 2 reliability work (reboot behavior, driver updates, signal loss) which only shows up on real hardware.
