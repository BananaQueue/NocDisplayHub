# Phase 1 — Foundation: Task Breakdown

Use this as the starting checklist when working with Claude Code. Read `CLAUDE.md` and `noc-display-app-spec.md` first. Work through these roughly in order — later tasks depend on earlier ones.

## 0. Prerequisites (do before writing code)
- [x] Confirm the hub's exact expected input resolution for its 2x3 preset — confirmed 2026-09-16: 1920x1080, real hardware
- [x] Confirm the hub's physical split-point pixel coordinates for that resolution — confirmed 2026-09-16 on real hardware: even split lines up with the physical bezels exactly for all five presets (1x1, 1x2, 2x1, 2x2, 2x3)
- [x] Confirm whether the hub does bezel compensation itself — confirmed 2026-09-16: not needed, even split already lines up across all presets
- [ ] Check whether the hub exposes any per-port status feed (serial/IP/CEC) — affects Phase 2, but worth knowing now
- [ ] List every dashboard/app that needs to be pinned, and for each: is it a browser URL or a native executable?

## 1. Project scaffolding
- [ ] New WPF (.NET) project, targeting current .NET LTS
- [ ] Add WebView2 SDK reference
- [ ] Set up basic project structure: `/Compositor` (rendering/window logic), `/Bindings` (cell-to-content logic), `/UI` (editor screens), `/Config` (layout/profile persistence)

## 2. Compositor window
- [ ] Create one fullscreen, borderless window sized to the hub's expected input resolution (from Task 0)
- [ ] Confirm the window renders correctly on the actual hardware and lines up with the hub's physical cuts before building anything else

## 3. Cell grid system
- [ ] Define a `Cell` model: index, pixel bounds, binding (type + value), status (healthy/degraded/dark/unbound)
- [ ] Support the hub's actual presets only: 1x1, 1x2, 2x1, 2x2, 2x3 — generate cell bounds per preset from Task 0's split-point data
- [ ] Implement preset switching that preserves bindings for hidden cells (see CLAUDE.md decision)

## 4. Cell content binding
- [ ] Browser cells: host a `WebView2` control per cell, navigate to the assigned URL
- [ ] Native app cells: launch the target executable, reparent its window into the cell region via `SetParent` + appropriate window style adjustments
- [ ] Persist bindings to disk (per profile) so they survive a restart

## 5. Kiosk styling
- [ ] Hide window chrome (title bar, borders) on all cells
- [ ] Confirm cells remain clickable/interactive (this is intentional — do not block input)

## 6. Editor UI — first pass
- [ ] Split picker (buttons for the 5 supported presets)
- [ ] Grid preview showing current bindings per cell, clickable to select
- [ ] Assignment panel: source type dropdown (native app / browser URL) + value field + apply action
- [ ] Wire the editor to the same binding/persistence layer the compositor reads from

## Definition of done for Phase 1
You can: pick a preset, assign a URL or app to each visible cell, apply it, and see it actually render correctly on the physical 6-monitor wall through the hub — with bindings surviving an app restart. Reliability (watchdog, went-dark detection, auto-recovery) is explicitly Phase 2 and not required yet.
