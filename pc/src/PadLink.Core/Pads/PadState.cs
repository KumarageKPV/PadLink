using PadLink.Protocol;

namespace PadLink.Core.Pads;

/// <summary>
/// The input part of a phone's packet: what a pad shows, before mapping to a pad type. The default
/// value is the neutral state: nothing pressed, sticks centred.
/// </summary>
public readonly record struct PadState
{
    public PadButtons Buttons { get; init; }

    /// <summary>Left stick, right is positive.</summary>
    public short Lx { get; init; }

    /// <summary>Left stick, up is positive.</summary>
    public short Ly { get; init; }

    public short Rx { get; init; }
    public short Ry { get; init; }
    public byte Lt { get; init; }
    public byte Rt { get; init; }

    public static PadState From(in InputPacket packet) => new()
    {
        Buttons = packet.Buttons,
        Lx = packet.Lx,
        Ly = packet.Ly,
        Rx = packet.Rx,
        Ry = packet.Ry,
        Lt = packet.Lt,
        Rt = packet.Rt,
    };
}
