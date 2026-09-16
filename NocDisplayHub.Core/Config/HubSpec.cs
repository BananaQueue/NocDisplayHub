namespace NocDisplayHub.Core.Config;

/// <summary>
/// Hub-specific facts that must come from the physical video-wall splitter's
/// manual or direct measurement — never estimated. Values below are
/// placeholders until confirmed (see PHASE_1_TASKS.md, Task 0).
/// </summary>
public static class HubSpec
{
    // TODO_HUB_SPEC: confirm the hub's expected combined input resolution for 2x3 mode.
    public const int InputWidth = 1920;
    public const int InputHeight = 1080;

    // TODO_HUB_SPEC: confirm whether the hub compensates for bezel gaps itself.
    public const bool HasBezelCompensation = false;

    // TODO_HUB_SPEC: confirm the hub's actual physical split-point pixel coordinates
    // per preset. Until then, PresetLayout divides InputWidth/InputHeight evenly,
    // which is very likely wrong for real bezel-corrected cuts.
}
