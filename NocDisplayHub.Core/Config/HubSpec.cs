namespace NocDisplayHub.Core.Config;

/// <summary>
/// Hub-specific facts that must come from the physical video-wall splitter's
/// manual or direct measurement — never estimated (see PHASE_1_TASKS.md, Task 0).
/// </summary>
public static class HubSpec
{
    // CONFIRMED on real hardware (2026-09-16): the hub's combined input for
    // 2x3 mode is 1920x1080, and PresetLayout's even 3x2 split lines up with
    // the physical monitor bezels exactly — no extra bezel-gap inset needed
    // for this hub/monitor combination in 2x3 mode.
    public const int InputWidth = 1920;
    public const int InputHeight = 1080;

    // Confirmed true for 2x3 mode (see above). Not yet confirmed for the
    // other presets (1x1, 1x2, 2x1, 2x2) — an even split happening to match
    // for 2x3 doesn't guarantee the hub cuts every mode evenly.
    public const bool HasBezelCompensation = false;

    // TODO_HUB_SPEC: confirm whether the hub exposes any per-port health/status
    // feed (serial/IP/CEC). Needed for real "went dark" (physical disconnect)
    // detection — Phase 2's watchdog currently only detects process/render
    // crashes, not an actual monitor going dark while its app keeps running.
}
