# NOC Multi-Monitor Display App — Project Spec

## Overview
A Windows desktop application for a single datacenter/NOC workstation that manages what appears on each connected monitor — pinning specific dashboards, graphs, or apps to specific screens, running unattended for long stretches. Positioned as a simpler, purpose-built alternative to general tools like DisplayFusion, tailored to a fixed NOC-style setup rather than everyday productivity use.

## Environment
- **Workstation:** Single desktop unit
- **Displays:** 6 monitors, connected via an HDMI hub
- **Hub behavior (key constraint):** The hub is a fixed-grid video-wall splitter (presets: 1x1, 1x2, 2x1, 2x2, 2x3) — it takes one combined video signal and physically cuts it across the monitors. It does not give Windows 6 independently addressable displays, so standard per-monitor app binding (as originally scoped) is not possible with this hardware. See "Architecture: software compositor" below for the workaround.
- **GPU:** Dedicated GPU present
- **Dashboard software:** Varies (mix of web-based and/or native apps — to be confirmed per-dashboard as they're integrated)
- **Use case:** Internal use only (not planned for resale — open question, see below)
- **Alerting:** Simple on-screen indicator is sufficient for v1 (no external alert pipeline like Slack/PagerDuty needed initially)
- **Testing:** Developer has direct access to test on the real hardware/environment

## Architecture: software compositor (workaround for the hub)
Because the hub only accepts one combined signal and physically slices it into a grid, Windows will only ever see **one virtual display** at whatever resolution the hub expects for its chosen grid mode. The app compensates by acting as its own compositor rather than relying on Windows' native multi-monitor APIs:

1. **One fullscreen borderless window** sized to match the hub's expected input resolution for its grid mode (get this exact figure from the hub's manual — don't estimate it).
2. **An internal layout grid** inside that window (e.g. a WPF `Grid`/`Canvas`) with 6 precisely-sized cells whose pixel boundaries match the hub's physical split points exactly.
3. **Content per cell:**
   - Web dashboards → one `WebView2` control per cell, each pointed at its own URL. This covers most of the "varies" dashboard case and is the simplest path.
   - Native apps → harder. Either reparent the app's window handle into the cell (Win32 `SetParent`, can be flaky with apps using hardware overlays), or capture the app's rendered output via the Windows Graphics Capture API and redraw it into the cell. **Decision:** since dashboard apps here are standard windowed apps (no hardware-accelerated rendering like GPU-rendered 3D views or video feeds), Win32 window reparenting is the simpler and lower-risk choice for v1 — capture-based rendering isn't needed unless a non-standard app turns up later. This also fits the decision that cells stay clickable/interactive: a reparented real window naturally passes through clicks, whereas a captured texture would need separate input-injection work to stay interactive.
4. **Bezel handling** — decide whether the hub does bezel compensation itself or whether the app needs to inset content per cell to fake the physical gap between monitors.

### Trade-off this introduces
Standard OS-level per-monitor disconnect detection (originally planned in Phase 2) no longer works, since Windows can't tell that one physical panel in the middle of the composited image went dark — that information, if available at all, would have to come from the hub's own status output (if it exposes one, e.g. serial/IP control). This needs to be confirmed with the hub before Phase 2 begins; if the hub has no per-port status feed, "monitor went dark" detection may need to drop to v2 or use a cruder approach (e.g. a small visible heartbeat pattern per cell that a person notices missing).

## Scope (v1)
Pure software. No custom hardware adapter. Native Windows app (or Electron, pending tech stack decision) running on the single workstation described above, built as a software compositor per the architecture above rather than relying on native OS multi-monitor APIs.

### In scope for v1
1. **App/dashboard-to-cell binding** (cells = compositor regions mapped to physical monitors, not OS-level monitors)
   - Assign a specific app, browser view, or URL to a specific cell
   - Bindings persist across reboots
   - Kiosk styling per cell: no chrome, minimal user interaction required. **Decision:** cells remain clickable/interactive at the physical workstation (e.g. clicking into a Grafana panel to drill in) rather than being a pure non-interactive display — kiosk styling hides app chrome but doesn't block interaction with the dashboard content itself. No lock mechanism against accidental changes is needed for this setup.
2. **Reliability core**
   - Watchdog: detect a bound app/dashboard crashing or closing, auto-relaunch. **Decision:** while the app is loading back in, the cell shows a visible "restarting" state rather than relaunching silently, so anyone glancing at the wall can see a recovery is in progress. No special-case handling is needed for the after-hours shutdown, since the operator closes the app deliberately before powering down the monitors — this is distinct from an unexpected crash and won't trigger watchdog/went-dark alerts.
   - Content-level "went dark" detection per cell (since true OS-level per-monitor disconnect detection isn't available with this hub — see architecture section) and show a simple on-screen indicator
   - Auto-recover the compositor window and cell layout after a reboot, driver update, or signal loss — no manual reconfiguration needed. **Decision:** the app auto-launches and restores the wall on Windows startup (auto-login into a kiosk session), rather than waiting for someone to log in manually first.
3. **Content refresh management**
   - Scheduled refresh for browser-based dashboards that otherwise go stale silently
   - Support splitting one monitor into a grid of smaller widgets (not just one full-screen app per monitor)
   - Basic frozen-content detection (e.g. screenshot diffing) to flag a graph that's stopped updating. **Decision:** uses the same red border indicator as the went-dark state (see reliability core above), rather than a separate visual — the activity log is the place to distinguish which specific condition triggered it.
4. **UI**
   - Visual layout editor: cells shown as a canvas/grid matching the physical monitor arrangement, click a cell to edit its settings — no nested settings dialogs
   - Layout profiles (e.g. save/restore the current 6-cell arrangement). **Decision:** v1 supports manual profile switching (operator-triggered); scheduled auto-switching (e.g. auto-apply a shift-handover layout at a set time) is noted for future-proofing and should be designed so it can be added later without reworking the profile system. Manual switching is available via a quick-switch hotkey while the wall is running, not only from within the editor screen.

## UI design: cell binding editor
The core editor screen has three parts, in this order:
1. **Split picker** — buttons for the hub's actual supported presets only (1x1, 1x2, 2x1, 2x2, 2x3). The app never offers a split the hardware can't produce.
2. **Grid preview** — the selected split renders as clickable cells. Clicking a cell selects it (highlighted border) and shows what's currently assigned to it, or "Unassigned."
3. **Assignment panel** — appears below the grid for whichever cell is selected. Fields: source type (native app vs. browser tab/URL) and the corresponding value (file path or URL). An "Apply to cell" action saves the binding and updates the cell's preview label immediately.

The source-type choice in this panel maps directly to which underlying binding mechanism the compositor uses for that cell (WebView2 for browser tabs, window reparenting or capture for native apps — see architecture section above).

**Decision:** Switching split presets (e.g. 2x3 down to 2x2) keeps bindings for cells no longer visible, rather than discarding them, so switching back later restores them automatically.

Editor and runtime ("wall") views are visually distinct: the editor uses a normal desktop app UI with a dark theme by default (to match typical dashboard tools); the wall itself stays near-invisible — dashboards fill the space, with app chrome reduced to a thin per-cell border color-shift for status (green = healthy, amber = degraded, red = went dark, gray = unbound) and, optionally, a single thin global status strip.

## Feature backlog (beyond core v1 scope)
**Hotkeys decision:** Profile quick-switch, global dim/blank, and manual snapshot export each get their own separate, dedicated hotkey rather than sharing one unified modifier+key scheme. Exact key assignments to be finalized during implementation.

Candidates to prioritize into v1 / v1.5 / v2, not yet assigned:
- **Cross-cell layouts** — since the compositor owns the whole canvas, a dashboard or alert banner could span multiple cells (e.g. 2x2 or full width), not just fixed single-cell rectangles. **Decision:** deferred past v1 — v1 keeps strictly to fixed single cells; spanning is added later once the basics are proven out.
- **Global dim/blank mode** — one action to blank the whole wall (shift end, burn-in prevention). **Decision:** reachable via an in-app hotkey, not left to physically powering off monitors/hub.
- ~~Idle/burn-in protection~~ — not needed: monitors are turned off after office hours, so the 24/7 burn-in risk this was meant to address doesn't apply.
- **Snapshot export** — save what the wall looked like at a point in time as one screen capture. **Decision:** manually triggered (hotkey or button), not on an automatic schedule — e.g. for capturing a moment during an incident, not for building a historical record.
- **Alert-driven auto-layout** — a monitoring alert could temporarily promote a specific dashboard to a larger cell or banner, then revert (needs a local webhook/API listener — leans v2 complexity). **Decision:** kept as a v2 candidate, not dropped, not pulled into v1.
- **Shift handover layout** — a saved profile specifically for shift start (status summary) vs. the normal working layout; combined with scheduled auto-switching (see profile-switching decision above), this could apply automatically at shift-change time rather than needing a manual trigger
- **Local activity log** — plain text log of layout switches, crashes/relaunches, and went-dark events for postmortems, without needing full alerting infrastructure. **Decision:** v1 is a plain text/log file on disk, viewed separately when needed — no in-app log viewer panel required. Every entry includes timestamp and which cell it applies to by default.

Deliberately avoided (scope creep risks, same trap DisplayFusion fell into): a plugin/scripting system for arbitrary dashboard types, and building out full external alerting integrations before they're actually needed.

### Explicitly out of scope for v1 (candidates for v2)
- Remote management / pushing config from another machine (deferred to ship v1 faster)
- Multi-workstation profile sync (not needed yet — single workstation)
- External alerting integrations (Slack, PagerDuty, email) — on-screen indicator covers v1
- Scripting/macro engine, advanced trigger conditions

## Procedural Roadmap
1. **Confirm hub specs** — Get the exact combined input resolution the hub expects for 2x3 mode, its physical split boundaries, whether it does bezel compensation, and whether it exposes any per-port status feed. This determines several downstream decisions and should happen before any code is written.
2. **Discovery & requirements** — Confirm the exact list of dashboards/apps to be pinned and how each is normally launched (browser URL vs native executable), since "varies" needs to be nailed down per-app before binding logic is built.
3. **Tech stack:** C# / .NET, WPF for the UI shell, WebView2 for browser-based dashboard cells, Win32 P/Invoke for window reparenting and display/compositor control. Chosen over Electron for tighter Win32 API access and lower resource use on an always-on kiosk box.
4. **Set up a test rig** — Already possible since the developer has access to the real hardware; use the actual workstation + HDMI hub + GPU combo for testing from the start, since compositor pixel-alignment and reliability bugs are hardware-specific.
5. **Build Phase 1 — Foundation:** the compositor window, cell layout grid, app/dashboard-to-cell binding, kiosk styling.
6. **Build Phase 2 — Reliability:** watchdog/auto-relaunch, cell-level went-dark detection with on-screen indicator, compositor window auto-recovery.
7. **Build Phase 3 — Content refresh:** scheduled refresh, sub-region grid binding within a cell, frozen-content detection.
8. **Build Phase 5 — Polish:** the visual layout editor UI, packaging, auto-start on boot.
9. **Pilot on the live workstation** — Run the v1 build in real conditions for a week or two before considering it "done," since this is the only workstation in scope. Pay particular attention to pixel-alignment against the hub's actual physical cuts.
10. **Document** — A short internal runbook: how to change what's pinned where, what the on-screen indicators mean, how to restart the app if needed.

## Timeline (solo developer, using Claude Code for scaffolding)
| Phase | Scope | Estimate |
|---|---|---|
| 1 — Foundation | Monitor detection, layout persistence, app binding, kiosk mode | 1.5–2.5 weeks |
| 2 — Reliability | Watchdog, disconnect detection, layout auto-recovery | 3–5 weeks (hardware-testing bound, least compressible) |
| 3 — Content refresh | Scheduled refresh, grid binding, frozen-content detection | 1.5–2 weeks |
| 5 — Polish | Visual layout editor, packaging | 1.5–2 weeks |
| **Total** | | **~7.5–11.5 weeks** |

Phase 2 remains the least compressible phase regardless of tooling, since it depends on observing real reboot/driver behavior rather than writing code.

## Open Questions
- **Internal vs. resale:** Currently uncertain. If there's any chance this gets packaged for other teams or sold later, worth flagging before v1 hard-codes assumptions specific to this one workstation.
- **Per-dashboard integration details:** Since dashboard software "varies," each one may need slightly different handling (browser kiosk mode vs. native app window management) — worth listing them out explicitly before Phase 1 starts.
- **IT/security constraints:** Not yet discussed — worth a quick check on whether a background watchdog process, auto-launching browsers, or an auto-login kiosk session raises any concerns in this environment.
- **Hub's expected input resolution and split boundaries:** Needed before Phase 1 starts — get the exact combined resolution the hub wants for 2x3 mode and the precise pixel coordinates where it physically cuts, straight from its manual or by testing, not by estimating.
- **Hub bezel compensation:** Confirm whether the hub compensates for bezel gaps itself or whether the app needs to inset content per cell.
- **Hub per-port status feed:** Confirm whether the hub exposes any way (serial, IP, HDMI CEC, etc.) to report an individual monitor's health, since Windows itself can't detect this through the composited signal. If none exists, "monitor went dark" detection needs a cruder cell-level approach (see architecture section) and may drop to v2.
- **Refresh interval scoping (deferred):** Whether the scheduled dashboard refresh interval is global (one setting for the whole wall) or per-cell is deliberately left open — decide once dashboard staleness is actually observed in practice, rather than guessing upfront.
