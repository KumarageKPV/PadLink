namespace PadLink.Core.Pads;

/// <summary>An Xbox 360 report as the driver takes it (ViGEmClient's XUSB_REPORT).</summary>
public readonly record struct X360Report
{
    public X360Buttons Buttons { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }

    /// <summary>Up is positive, as on the phone.</summary>
    public short ThumbLX { get; init; }

    public short ThumbLY { get; init; }
    public short ThumbRX { get; init; }
    public short ThumbRY { get; init; }
}

/// <summary>A DualShock 4 report as the driver takes it (ViGEmClient's DS4_REPORT).</summary>
public readonly record struct Ds4Report
{
    /// <summary>DS4_REPORT's button word: <see cref="Buttons"/> with <see cref="Hat"/> in its low 4 bits.</summary>
    public ushort ButtonWord => (ushort)((ushort)Buttons | (byte)Hat);

    /// <summary>128 is the centre, and up is 0.</summary>
    public byte ThumbLX { get; init; }

    public byte ThumbLY { get; init; }
    public byte ThumbRX { get; init; }
    public byte ThumbRY { get; init; }
    public Ds4Buttons Buttons { get; init; }
    public Ds4Hat Hat { get; init; }
    public Ds4Special Special { get; init; }
    public byte TriggerL { get; init; }
    public byte TriggerR { get; init; }
}

/// <summary>XUSB_REPORT's button bits.</summary>
[Flags]
public enum X360Buttons : ushort
{
    None = 0,
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>
/// DS4_REPORT's button bits above the hat. L2 and R2 have bits too, but the mapping sends only the
/// analog triggers (protocol/SPEC.md, "Mapping to pad reports").
/// </summary>
[Flags]
public enum Ds4Buttons : ushort
{
    None = 0,
    Square = 1 << 4,
    Cross = 1 << 5,
    Circle = 1 << 6,
    Triangle = 1 << 7,
    ShoulderLeft = 1 << 8,
    ShoulderRight = 1 << 9,
    Share = 1 << 12,
    Options = 1 << 13,
    ThumbLeft = 1 << 14,
    ThumbRight = 1 << 15,
}

/// <summary>The DualShock 4 hat, the low 4 bits of DS4_REPORT's button word.</summary>
public enum Ds4Hat : byte
{
    North = 0,
    NorthEast = 1,
    East = 2,
    SouthEast = 3,
    South = 4,
    SouthWest = 5,
    West = 6,
    NorthWest = 7,
    Neutral = 8,
}

/// <summary>DS4_REPORT's special byte. The touchpad click has a bit too, but no phone input maps to it.</summary>
[Flags]
public enum Ds4Special : byte
{
    None = 0,
    Ps = 1 << 0,
}
