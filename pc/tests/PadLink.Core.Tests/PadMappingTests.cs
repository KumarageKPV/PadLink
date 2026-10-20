using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PadLink.Core.Pads;
using PadLink.Protocol;
using PadLink.Protocol.Tests;

namespace PadLink.Core.Tests;

public class PadMappingTests
{
    private const string File = "mapping.json";

    private const PadButtons Dpad = PadButtons.DpadUp | PadButtons.DpadDown | PadButtons.DpadLeft | PadButtons.DpadRight;

    public static TheoryData<short> Sticks => [.. Vectors.File(File)["sticks"]!.AsArray().Select(v => v!["value"]!.GetValue<short>())];
    public static TheoryData<int> Dpads => [.. Vectors.File(File)["dpad"]!.AsArray().Select(v => v!["buttons"]!.GetValue<int>())];

    // protocol/SPEC.md, "Button bits": every bit but the d-pad (which has its own vectors) and bit 15.
    private static readonly (PadButtons Bit, X360Buttons X360, Ds4Buttons Ds4, Ds4Special Special)[] BitTable =
    [
        (PadButtons.FaceBottom, X360Buttons.A, Ds4Buttons.Cross, Ds4Special.None),
        (PadButtons.FaceRight, X360Buttons.B, Ds4Buttons.Circle, Ds4Special.None),
        (PadButtons.FaceLeft, X360Buttons.X, Ds4Buttons.Square, Ds4Special.None),
        (PadButtons.FaceTop, X360Buttons.Y, Ds4Buttons.Triangle, Ds4Special.None),
        (PadButtons.ShoulderLeft, X360Buttons.LeftShoulder, Ds4Buttons.ShoulderLeft, Ds4Special.None),
        (PadButtons.ShoulderRight, X360Buttons.RightShoulder, Ds4Buttons.ShoulderRight, Ds4Special.None),
        (PadButtons.Back, X360Buttons.Back, Ds4Buttons.Share, Ds4Special.None),
        (PadButtons.Start, X360Buttons.Start, Ds4Buttons.Options, Ds4Special.None),
        (PadButtons.Guide, X360Buttons.Guide, Ds4Buttons.None, Ds4Special.Ps),
        (PadButtons.StickLeft, X360Buttons.LeftThumb, Ds4Buttons.ThumbLeft, Ds4Special.None),
        (PadButtons.StickRight, X360Buttons.RightThumb, Ds4Buttons.ThumbRight, Ds4Special.None),
    ];

    public static TheoryData<PadButtons, X360Buttons, Ds4Buttons, Ds4Special> ButtonBits
    {
        get
        {
            var data = new TheoryData<PadButtons, X360Buttons, Ds4Buttons, Ds4Special>();
            foreach (var (bit, x360, ds4, special) in BitTable)
                data.Add(bit, x360, ds4, special);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Sticks))]
    public void Stick_vector_maps_to_its_xbox_value_and_ds4_bytes(short value)
    {
        var vector = Vectors.File(File)["sticks"]!.AsArray().Single(v => v!["value"]!.GetValue<short>() == value)!;
        var x360 = vector["x360"]!.GetValue<short>();
        var ds4X = vector["ds4X"]!.GetValue<byte>();
        var ds4Y = vector["ds4Y"]!.GetValue<byte>();
        var state = new PadState { Lx = value, Ly = value, Rx = value, Ry = value };

        var xbox = PadMapping.ToX360(state);
        var ds4 = PadMapping.ToDs4(state);

        Assert.Equal((x360, x360, x360, x360), (xbox.ThumbLX, xbox.ThumbLY, xbox.ThumbRX, xbox.ThumbRY));
        Assert.Equal((ds4X, ds4Y, ds4X, ds4Y), (ds4.ThumbLX, ds4.ThumbLY, ds4.ThumbRX, ds4.ThumbRY));
    }

    [Fact]
    public void Each_stick_axis_maps_to_its_own_report_axis()
    {
        // Values whose Xbox values and DualShock 4 bytes all differ, so a swap of any two axes shows.
        var state = new PadState { Lx = -32768, Ly = -256, Rx = 1, Ry = 255 };

        var xbox = PadMapping.ToX360(state);
        var ds4 = PadMapping.ToDs4(state);

        Assert.Equal(((short)-32768, (short)-256, (short)1, (short)255), (xbox.ThumbLX, xbox.ThumbLY, xbox.ThumbRX, xbox.ThumbRY));
        Assert.Equal((Stick(-32768, "ds4X"), Stick(-256, "ds4Y"), Stick(1, "ds4X"), Stick(255, "ds4Y")), (ds4.ThumbLX, ds4.ThumbLY, ds4.ThumbRX, ds4.ThumbRY));
        Assert.Equal(4, new[] { ds4.ThumbLX, ds4.ThumbLY, ds4.ThumbRX, ds4.ThumbRY }.Distinct().Count());
    }

    [Theory]
    [InlineData(PadButtons.None, 0x0008)]
    [InlineData(PadButtons.FaceBottom | PadButtons.DpadUp | PadButtons.DpadRight, 0x0021)]
    [InlineData(PadButtons.StickRight | PadButtons.FaceLeft | PadButtons.DpadLeft, 0x8016)]
    public void The_ds4_button_word_has_the_hat_in_its_low_4_bits(PadButtons buttons, int word)
    {
        Assert.Equal(word, PadMapping.ToDs4(new PadState { Buttons = buttons }).ButtonWord);
    }

    [Theory]
    [MemberData(nameof(Dpads))]
    public void Dpad_vector_maps_to_its_ds4_hat_and_to_the_same_xbox_dpad_buttons(int buttons)
    {
        var vector = Vectors.File(File)["dpad"]!.AsArray().Single(v => v!["buttons"]!.GetValue<int>() == buttons)!;
        var pressed = PadButtons.None;
        var xboxDpad = X360Buttons.None;
        foreach (var name in vector["pressed"]!.AsArray().Select(n => n!.GetValue<string>()))
        {
            pressed |= Enum.Parse<PadButtons>(name, ignoreCase: true);
            xboxDpad |= Enum.Parse<X360Buttons>(name, ignoreCase: true);
        }
        Assert.Equal((PadButtons)buttons, pressed);
        var state = new PadState { Buttons = pressed };

        var ds4 = PadMapping.ToDs4(state);

        Assert.Equal(vector["ds4Hat"]!.GetValue<byte>(), (byte)ds4.Hat);
        Assert.Equal(Ds4Buttons.None, ds4.Buttons);
        Assert.Equal(xboxDpad, PadMapping.ToX360(state).Buttons);
    }

    [Theory]
    [MemberData(nameof(ButtonBits))]
    public void Button_bit_maps_to_its_xbox_and_ds4_button(PadButtons bit, X360Buttons x360, Ds4Buttons ds4, Ds4Special special)
    {
        var state = new PadState { Buttons = bit };

        var report = PadMapping.ToDs4(state);

        Assert.Equal(x360, PadMapping.ToX360(state).Buttons);
        Assert.Equal((ds4, special, Ds4Hat.Neutral), (report.Buttons, report.Special, report.Hat));
    }

    [Fact]
    public void Every_button_combination_maps_bit_by_bit_and_ignores_bit_15()
    {
        // The lookup tables split the button word by byte, so check every word against the per-bit table.
        for (var word = 0; word <= ushort.MaxValue; word++)
        {
            var buttons = (PadButtons)word;
            var x360 = X360Buttons.None;
            var ds4 = Ds4Buttons.None;
            var special = Ds4Special.None;
            foreach (var (bit, x360Bit, ds4Bit, specialBit) in BitTable)
            {
                if (!buttons.HasFlag(bit))
                    continue;
                x360 |= x360Bit;
                ds4 |= ds4Bit;
                special |= specialBit;
            }
            var dpadOnly = new PadState { Buttons = buttons & Dpad };
            var state = new PadState { Buttons = buttons };

            var ds4Report = PadMapping.ToDs4(state);

            Assert.Equal(x360 | PadMapping.ToX360(dpadOnly).Buttons, PadMapping.ToX360(state).Buttons);
            Assert.Equal((ds4, special, PadMapping.ToDs4(dpadOnly).Hat), (ds4Report.Buttons, ds4Report.Special, ds4Report.Hat));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(255)]
    public void Triggers_pass_through_to_both_reports(byte value)
    {
        var state = new PadState { Lt = value, Rt = (byte)(255 - value) };

        var xbox = PadMapping.ToX360(state);
        var ds4 = PadMapping.ToDs4(state);

        Assert.Equal((value, (byte)(255 - value)), (xbox.LeftTrigger, xbox.RightTrigger));
        Assert.Equal((value, (byte)(255 - value)), (ds4.TriggerL, ds4.TriggerR));
    }

    [Fact]
    public void The_neutral_state_releases_and_centres_both_reports()
    {
        Assert.Equal(default, PadMapping.ToX360(default));
        Assert.Equal(new Ds4Report
        {
            ThumbLX = 128,
            ThumbLY = 128,
            ThumbRX = 128,
            ThumbRY = 128,
            Hat = Ds4Hat.Neutral,
        }, PadMapping.ToDs4(default));
    }

    [Fact]
    public void The_state_from_a_packet_holds_its_buttons_sticks_and_triggers()
    {
        var input = Vectors.File("input.json");
        Assert.True(InputPacket.TryRead(Vectors.Hex(Vectors.Get("input.json", "valid", "typical")["hex"]), Vectors.Hex(input["sessionKey"]), out var packet, out _));

        var state = PadState.From(packet);

        Assert.Equal(new PadState
        {
            Buttons = packet.Buttons,
            Lx = packet.Lx,
            Ly = packet.Ly,
            Rx = packet.Rx,
            Ry = packet.Ry,
            Lt = packet.Lt,
            Rt = packet.Rt,
        }, state);
    }

    // The report values are what the driver takes, so they must match the client library's own.
    [Fact]
    public void Report_values_match_the_client_library()
    {
        Assert.Equal(
            [Xbox360Button.Up.Value, Xbox360Button.Down.Value, Xbox360Button.Left.Value, Xbox360Button.Right.Value,
             Xbox360Button.Start.Value, Xbox360Button.Back.Value, Xbox360Button.LeftThumb.Value, Xbox360Button.RightThumb.Value,
             Xbox360Button.LeftShoulder.Value, Xbox360Button.RightShoulder.Value, Xbox360Button.Guide.Value,
             Xbox360Button.A.Value, Xbox360Button.B.Value, Xbox360Button.X.Value, Xbox360Button.Y.Value],
            new[] { X360Buttons.DpadUp, X360Buttons.DpadDown, X360Buttons.DpadLeft, X360Buttons.DpadRight,
                    X360Buttons.Start, X360Buttons.Back, X360Buttons.LeftThumb, X360Buttons.RightThumb,
                    X360Buttons.LeftShoulder, X360Buttons.RightShoulder, X360Buttons.Guide,
                    X360Buttons.A, X360Buttons.B, X360Buttons.X, X360Buttons.Y }.Select(b => (ushort)b));
        Assert.Equal(
            [DualShock4Button.Square.Value, DualShock4Button.Cross.Value, DualShock4Button.Circle.Value, DualShock4Button.Triangle.Value,
             DualShock4Button.ShoulderLeft.Value, DualShock4Button.ShoulderRight.Value, DualShock4Button.Share.Value,
             DualShock4Button.Options.Value, DualShock4Button.ThumbLeft.Value, DualShock4Button.ThumbRight.Value],
            new[] { Ds4Buttons.Square, Ds4Buttons.Cross, Ds4Buttons.Circle, Ds4Buttons.Triangle,
                    Ds4Buttons.ShoulderLeft, Ds4Buttons.ShoulderRight, Ds4Buttons.Share,
                    Ds4Buttons.Options, Ds4Buttons.ThumbLeft, Ds4Buttons.ThumbRight }.Select(b => (ushort)b));
        Assert.Equal(
            [DualShock4DPadDirection.North.Value, DualShock4DPadDirection.Northeast.Value, DualShock4DPadDirection.East.Value,
             DualShock4DPadDirection.Southeast.Value, DualShock4DPadDirection.South.Value, DualShock4DPadDirection.Southwest.Value,
             DualShock4DPadDirection.West.Value, DualShock4DPadDirection.Northwest.Value, DualShock4DPadDirection.None.Value],
            new[] { Ds4Hat.North, Ds4Hat.NorthEast, Ds4Hat.East, Ds4Hat.SouthEast, Ds4Hat.South, Ds4Hat.SouthWest,
                    Ds4Hat.West, Ds4Hat.NorthWest, Ds4Hat.Neutral }.Select(h => (ushort)h));
        Assert.Equal((ushort)Ds4Special.Ps, DualShock4SpecialButton.Ps.Value);
    }

    // Mapping runs on the hot path (UDP receive → pad submit), which must not allocate (CLAUDE.md, Rules).
    [Fact]
    public void Mapping_allocates_nothing()
    {
        var state = new PadState { Buttons = (PadButtons)0x7fff, Lx = -32768, Ly = 32767, Rx = -1, Ry = 1, Lt = 9, Rt = 250 };
        long checksum = Map(state); // warm-up: JIT and the tables' static constructor

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            checksum += Map(state);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(10_001 * Map(state), checksum);
    }

    private static byte Stick(short value, string field) =>
        Vectors.File(File)["sticks"]!.AsArray().Single(v => v!["value"]!.GetValue<short>() == value)![field]!.GetValue<byte>();

    private static long Map(in PadState state)
    {
        var xbox = PadMapping.ToX360(state);
        var ds4 = PadMapping.ToDs4(state);
        return (long)xbox.Buttons + xbox.ThumbLX + (long)ds4.Buttons + (byte)ds4.Hat + ds4.ThumbLY;
    }
}
