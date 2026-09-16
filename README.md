# NOC Display Hub

A Windows desktop app for a single NOC/datacenter workstation. It drives 6 monitors connected through a fixed-grid HDMI video-wall splitter, pinning specific dashboards/apps to specific screen regions and keeping them running reliably, unattended, for long stretches.

See [noc-display-app-spec.md](noc-display-app-spec.md) for the full product spec and [CLAUDE.md](CLAUDE.md) for the key architectural decisions.

## The one thing to never forget

**Windows only ever sees ONE virtual display.** The hub physically slices one combined video signal into a grid — it does not give the OS 6 independent monitors. The app is its own compositor: one fullscreen borderless window, subdivided into cells that match the hub's physical cut points.

## Status

All five spec phases are implemented and verified end-to-end (editor → persistence → wall render), but the app has **not yet run against the real hub hardware**. Everything sizing-related uses placeholder constants until the real numbers are confirmed:

- [ ] Hub's exact input resolution and physical split-point coordinates (`TODO_HUB_SPEC` in [HubSpec.cs](NocDisplayHub.Core/Config/HubSpec.cs))
- [ ] Whether the hub does bezel compensation
- [ ] Whether the hub exposes a per-port health/status feed (would upgrade the current crash-only "went dark" detection to a true physical-disconnect signal)
- [ ] Pilot run on the actual 6-monitor wall

See [PHASE_1_TASKS.md](PHASE_1_TASKS.md) for the original task breakdown (Phase 1 only — later phases were scoped directly from the spec's roadmap).

## What's built

- **Compositor** ([NocDisplayHub/Compositor](NocDisplayHub/Compositor)) — the fullscreen wall window. Browser cells run in `WebView2`; native app cells are reparented in via Win32 (`SetParent`/`SetWindowPos`). A watchdog relaunches crashed content with a visible "Restarting…" state, never silently. A scheduled-refresh timer reloads browser cells periodically, and a frozen-content check screenshots each browser cell to flag one that's stopped updating.
- **Editor** ([NocDisplayHub/UI](NocDisplayHub/UI)) — split picker, clickable grid preview, and an assignment panel. A cell can also be split into its own sub-grid of smaller widgets, edited by drilling into it (no nested dialogs).
- **Core** ([NocDisplayHub.Core](NocDisplayHub.Core)) — framework-agnostic model and logic: cell/preset layout math, binding persistence (JSON profile on disk), the activity log, and the frozen-content state machine. This is the fully unit-tested layer (see [NocDisplayHub.Tests](NocDisplayHub.Tests)).
- **Startup** ([NocDisplayHub/Startup](NocDisplayHub/Startup)) — optional Windows auto-start registration (a per-user `Run` key), toggled from the editor rather than forced on.

## Running it

```bash
dotnet run --project NocDisplayHub
```

Boots straight into the wall (reading the last-saved profile), matching how it behaves after a real reboot. Pass `--edit` to open the layout editor instead:

```bash
dotnet run --project NocDisplayHub -- --edit
```

Layout and bindings persist to `%LOCALAPPDATA%\NocDisplayHub\profile.json`; watchdog/refresh/frozen-content events log to `%LOCALAPPDATA%\NocDisplayHub\activity.log`.

## Testing

```bash
dotnet test NocDisplayHub.Tests
```

## Publishing

Release builds are configured as a self-contained, single-file `win-x64` executable (kept out of normal `dotnet build`/`dotnet run` so the dev loop stays fast):

```bash
dotnet publish NocDisplayHub -c Release
```

Output lands in `NocDisplayHub/bin/Release/net10.0-windows/win-x64/publish/`.

## Explicitly out of scope for v1

Cross-cell spanning layouts, remote management, multi-workstation sync, external alerting integrations (Slack/PagerDuty/email), and a scripting/plugin engine. See the spec for the full list and rationale.
