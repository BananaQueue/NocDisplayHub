namespace NocDisplayHub.Core.Bindings;

/// <summary>The hub's actual supported split presets. The app never offers a split the hardware can't produce.</summary>
public enum Preset
{
    OneByOne,
    OneByTwo,
    TwoByOne,
    TwoByTwo,
    TwoByThree,
}
