namespace PadLink.Core.Pads;

/// <summary>
/// Creates virtual pads and feeds them (docs/design.md, "Virtual controller layer"). Every pad access
/// goes through this interface; unit tests use <see cref="FakePadBackend"/>.
/// </summary>
/// <remarks>
/// Calls for different pads may come from different threads, but each pad has one writer thread:
/// don't submit to a pad from two threads. Stop submitting to a pad before removing it: a Submit
/// already under way when Remove runs does nothing, and a later one throws. Stop every Submit before
/// Dispose.
/// </remarks>
public interface IVirtualPadBackend : IDisposable
{
    /// <summary>
    /// What a game sent to a pad. Raised on a driver thread, where an exception ends the process, so
    /// handlers must not throw. An Xbox 360 pad gets its first one, with its LED number, within about
    /// 20 ms of being created, possibly before <see cref="Create"/> returns.
    /// </summary>
    event Action<PadId, PadFeedback>? Feedback;

    /// <summary>Plugs in a new pad with a neutral state.</summary>
    PadId Create(PadType type);

    /// <summary>
    /// Sends the pad its whole state, mapped for its type. Allocates nothing, since this is the end of
    /// the hot path. Throws <see cref="ArgumentException"/> for a pad this backend doesn't hold.
    /// </summary>
    void Submit(PadId pad, in PadState state);

    /// <summary>Unplugs the pad. Throws <see cref="ArgumentException"/> for a pad this backend doesn't hold.</summary>
    void Remove(PadId pad);

    /// <summary>
    /// The XInput user index (0–3) of an Xbox 360 pad: the LED number the driver reported for it. Null
    /// before the driver reports one, for a fifth Xbox 360 pad, and for DualShock 4 pads.
    /// </summary>
    int? ReadUserIndex(PadId pad);
}

public enum PadType
{
    X360,
    Ds4,
}

/// <summary>A pad a backend created. Ids are never reused within a backend.</summary>
public readonly record struct PadId(int Value);

/// <summary>Rumble, and the player LED (Xbox 360) or lightbar colour (DualShock 4), that a game sent.</summary>
public readonly record struct PadFeedback
{
    public byte LargeMotor { get; init; }
    public byte SmallMotor { get; init; }

    /// <summary>
    /// The Xbox 360 player LED as the driver reports it: 0–3, or 255 for a pad that XInput gave no user
    /// index. Null for DualShock 4 pads.
    /// </summary>
    public byte? LedNumber { get; init; }

    /// <summary>The DualShock 4 lightbar colour. Null for Xbox 360 pads.</summary>
    public Rgb? Lightbar { get; init; }
}

public readonly record struct Rgb(byte Red, byte Green, byte Blue);
