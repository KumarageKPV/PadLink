using PadLink.Protocol;

namespace PadLink.Core.Pads;

/// <summary>
/// Phone input to pad reports (protocol/SPEC.md, "Mapping to pad reports"). These are pure functions
/// on the hot path, so they allocate nothing: button words map through tables built once per pad
/// type, one table per byte of the word.
/// </summary>
public static class PadMapping
{
    private const int DpadShift = 11;

    // protocol/SPEC.md, "Button bits". Bit 15 is reserved, and the mapping ignores it.
    private static readonly (PadButtons Phone, X360Buttons X360, Ds4Buttons Ds4)[] Bits =
    [
        (PadButtons.FaceBottom, X360Buttons.A, Ds4Buttons.Cross),
        (PadButtons.FaceRight, X360Buttons.B, Ds4Buttons.Circle),
        (PadButtons.FaceLeft, X360Buttons.X, Ds4Buttons.Square),
        (PadButtons.FaceTop, X360Buttons.Y, Ds4Buttons.Triangle),
        (PadButtons.ShoulderLeft, X360Buttons.LeftShoulder, Ds4Buttons.ShoulderLeft),
        (PadButtons.ShoulderRight, X360Buttons.RightShoulder, Ds4Buttons.ShoulderRight),
        (PadButtons.Back, X360Buttons.Back, Ds4Buttons.Share),
        (PadButtons.Start, X360Buttons.Start, Ds4Buttons.Options),
        (PadButtons.Guide, X360Buttons.Guide, Ds4Buttons.None), // PS is in the special byte
        (PadButtons.StickLeft, X360Buttons.LeftThumb, Ds4Buttons.ThumbLeft),
        (PadButtons.StickRight, X360Buttons.RightThumb, Ds4Buttons.ThumbRight),
        (PadButtons.DpadUp, X360Buttons.DpadUp, Ds4Buttons.None), // DualShock 4: the hat
        (PadButtons.DpadDown, X360Buttons.DpadDown, Ds4Buttons.None),
        (PadButtons.DpadLeft, X360Buttons.DpadLeft, Ds4Buttons.None),
        (PadButtons.DpadRight, X360Buttons.DpadRight, Ds4Buttons.None),
    ];

    // Low byte: bits 0–7. High byte: bits 8–14.
    private static readonly ushort[] X360Low = Table(0, 256, bit => (ushort)bit.X360);
    private static readonly ushort[] X360High = Table(8, 128, bit => (ushort)bit.X360);
    private static readonly ushort[] Ds4Low = Table(0, 256, bit => (ushort)bit.Ds4);
    private static readonly ushort[] Ds4High = Table(8, 128, bit => (ushort)bit.Ds4);
    private static readonly Ds4Hat[] Hats = HatTable();

    public static X360Report ToX360(in PadState state)
    {
        var buttons = (int)state.Buttons;
        return new X360Report
        {
            Buttons = (X360Buttons)(X360Low[buttons & 0xff] | X360High[(buttons >> 8) & 0x7f]),
            LeftTrigger = state.Lt,
            RightTrigger = state.Rt,
            ThumbLX = state.Lx,
            ThumbLY = state.Ly,
            ThumbRX = state.Rx,
            ThumbRY = state.Ry,
        };
    }

    public static Ds4Report ToDs4(in PadState state)
    {
        var buttons = (int)state.Buttons;
        return new Ds4Report
        {
            ThumbLX = Ds4StickX(state.Lx),
            ThumbLY = Ds4StickY(state.Ly),
            ThumbRX = Ds4StickX(state.Rx),
            ThumbRY = Ds4StickY(state.Ry),
            Buttons = (Ds4Buttons)(Ds4Low[buttons & 0xff] | Ds4High[(buttons >> 8) & 0x7f]),
            Hat = Hats[(buttons >> DpadShift) & 0xf],
            Special = (state.Buttons & PadButtons.Guide) != 0 ? Ds4Special.Ps : Ds4Special.None,
            TriggerL = state.Lt,
            TriggerR = state.Rt,
        };
    }

    /// <summary>An X axis as a DualShock 4 byte: <c>(v + 32768) &gt;&gt; 8</c>.</summary>
    public static byte Ds4StickX(short value) => (byte)((value + 32768) >> 8);

    /// <summary>
    /// A Y axis as a DualShock 4 byte, flipped since the DualShock 4 reports up as 0:
    /// <c>(min(-v, 32767) + 32768) &gt;&gt; 8</c>.
    /// </summary>
    public static byte Ds4StickY(short value) => (byte)((Math.Min(-value, short.MaxValue) + 32768) >> 8);

    // What each value of one byte of the button word maps to, bit by bit.
    private static ushort[] Table(int shift, int size, Func<(PadButtons Phone, X360Buttons X360, Ds4Buttons Ds4), ushort> pick)
    {
        var table = new ushort[size];
        for (var i = 0; i < size; i++)
        {
            var buttons = (PadButtons)(i << shift);
            foreach (var bit in Bits)
            {
                if (buttons.HasFlag(bit.Phone))
                    table[i] |= pick(bit);
            }
        }
        return table;
    }

    // Indexed by the 4 d-pad bits shifted down: up 1, down 2, left 4, right 8. Opposite directions
    // cancel, and what's left picks the direction.
    private static Ds4Hat[] HatTable()
    {
        Ds4Hat[,] byXY =
        {
            { Ds4Hat.SouthWest, Ds4Hat.West, Ds4Hat.NorthWest },
            { Ds4Hat.South, Ds4Hat.Neutral, Ds4Hat.North },
            { Ds4Hat.SouthEast, Ds4Hat.East, Ds4Hat.NorthEast },
        };
        var hats = new Ds4Hat[16];
        for (var dpad = 0; dpad < hats.Length; dpad++)
        {
            var x = ((dpad >> 3) & 1) - ((dpad >> 2) & 1); // right − left
            var y = (dpad & 1) - ((dpad >> 1) & 1); // up − down
            hats[dpad] = byXY[x + 1, y + 1];
        }
        return hats;
    }
}
