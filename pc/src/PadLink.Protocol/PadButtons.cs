namespace PadLink.Protocol;

/// <summary>
/// The input packet's button bits. They are positional, so each means the same button on both pad
/// types (protocol/SPEC.md, "Button bits").
/// </summary>
[Flags]
public enum PadButtons : ushort
{
    None = 0,
    FaceBottom = 1 << 0, // A / Cross
    FaceRight = 1 << 1, // B / Circle
    FaceLeft = 1 << 2, // X / Square
    FaceTop = 1 << 3, // Y / Triangle
    ShoulderLeft = 1 << 4, // LB / L1
    ShoulderRight = 1 << 5, // RB / R1
    Back = 1 << 6, // Back / Share
    Start = 1 << 7, // Start / Options
    Guide = 1 << 8, // Guide / PS
    StickLeft = 1 << 9, // L3
    StickRight = 1 << 10, // R3
    DpadUp = 1 << 11,
    DpadDown = 1 << 12,
    DpadLeft = 1 << 13,
    DpadRight = 1 << 14,

    /// <summary>The phone never sets it, the codec passes it through, and the pad mapping ignores it.</summary>
    Reserved = 1 << 15,
}
