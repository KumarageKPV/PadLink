using System.Net;
using PadLink.Protocol.Control;

namespace PadLink.Core.Control;

/// <summary>
/// Gives joining phones their slots: the slot manager implements it (docs/design.md, "PC companion app
/// design"). <see cref="ControlServer"/> never calls it from two threads at once.
/// </summary>
public interface IJoinHandler
{
    /// <summary>
    /// A phone passed <c>pair</c> or <c>auth</c>. Returns its <c>welcome</c>, or null when every slot is
    /// taken, which the phone hears as <c>full</c>. The session key is cleared once this returns, so copy
    /// it (<see cref="Input.InputReceiver.Open"/> does).
    /// </summary>
    /// <remarks>
    /// The <c>deviceId</c> may hold a slot already, through an older connection: the phone reconnected
    /// before the PC saw the old link drop. Move that slot to the new phone. The server then closes the
    /// old connection, and <see cref="Leave"/> for the old phone comes after this call.
    /// </remarks>
    Welcome? Join(ConnectedPhone phone, ReadOnlySpan<byte> sessionKey);

    /// <summary>
    /// A phone that <see cref="Join"/> welcomed is gone: it said <c>bye</c>, its link dropped or went
    /// quiet, it broke the protocol, a newer connection took its slot over, or the server stopped. Called
    /// once per welcomed phone.
    /// </summary>
    void Leave(ConnectedPhone phone);
}

/// <summary>
/// A phone that passed the join, as its <c>hello</c> described it. Each connection gets its own, so
/// after a takeover the old and new phone share a <see cref="DeviceId"/> but not a reference.
/// </summary>
public sealed class ConnectedPhone
{
    private int _battery = -1;

    internal ConnectedPhone(Hello hello, IPAddress address)
    {
        DeviceId = hello.DeviceId;
        DeviceName = hello.DeviceName;
        Platform = hello.Platform;
        AppVersion = hello.AppVersion;
        Address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public PhonePlatform Platform { get; }

    public string AppVersion { get; }

    /// <summary>The IP of the control connection: the only IP the slot's input is taken from (protocol/SPEC.md, "Packet rules").</summary>
    public IPAddress Address { get; }

    /// <summary>The battery level, 0–100, from the phone's latest <c>battery</c>, or null before its first.</summary>
    public int? Battery
    {
        get => Volatile.Read(ref _battery) is var pct and >= 0 ? pct : null;
        internal set => Volatile.Write(ref _battery, value ?? -1);
    }

    public override string ToString() => $"{DeviceName} ({DeviceId}) at {Address}";
}
