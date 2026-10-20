using System.Net;
using System.Net.Sockets;
using PadLink.Core.Input;
using PadLink.Core.Pads;
using PadLink.Protocol;
using PadLink.Protocol.Tests;

namespace PadLink.Core.Tests;

// Each test sends real datagrams over loopback and waits until the receiver has handled them.
public sealed class InputReceiverTests : IDisposable
{
    private const string File = "input.json";
    private const int Slot = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] Key = Vectors.Hex(Vectors.File(File)["sessionKey"]);
    private static readonly byte[] OtherKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly PadState Pressed = new() { Buttons = PadButtons.FaceBottom | PadButtons.DpadLeft, Lx = 1200, Ly = -5, Rt = 90 };
    private static readonly PadState Other = new() { Buttons = PadButtons.ShoulderRight, Ry = 300 };

    private readonly FakePadBackend _backend = new();
    private readonly InputReceiver _receiver;
    private readonly Socket _phone = PhoneSocket();

    public InputReceiverTests()
    {
        _receiver = new InputReceiver(_backend, new IPEndPoint(IPAddress.Loopback, 0));
    }

    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    public void Dispose()
    {
        _receiver.Dispose();
        _phone.Dispose();
        _backend.Dispose();
    }

    [Fact]
    public void A_valid_packet_reaches_its_pad_and_its_sentMs_and_port_are_kept()
    {
        var pad = OpenWithPad();

        Send(Input(sequence: 10, Pressed, sentMs: 1234));

        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
        Assert.True(_receiver.TryGetSlot(Slot, out var input));
        Assert.Equal(new SlotInput(10, 1234, PortOf(_phone)), input);
        Assert.Equal((1, 1), (_receiver.Accepted, _receiver.Submitted));
    }

    [Fact]
    public void Packets_with_a_wrong_mac_are_dropped_and_do_not_move_the_mark()
    {
        var pad = OpenWithPad();
        var zeroMac = Input(sequence: 100, Other);
        zeroMac.AsSpan(InputPacket.MacOffset).Clear();

        Send(Vectors.Flipped(Input(sequence: 100, Other), InputPacket.Size * 8 - 1));
        Send(Input(sequence: 100, Other, key: OtherKey));
        Send(zeroMac);
        Send(Input(sequence: 5, Pressed));

        Assert.Equal(3, _receiver.Dropped(InputDrop.Mac));
        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
    }

    // The address is checked before the mac, so a flood from other hosts costs no HMAC (docs/decisions.md).
    [Fact]
    public void A_packet_from_another_ip_is_dropped_before_its_mac_is_checked()
    {
        OpenWithPad(phone: IPAddress.Parse("127.0.0.2"));

        Send(Input(sequence: 1, Pressed));
        Send(Input(sequence: 2, Pressed, key: OtherKey));

        Assert.Equal((2, 0, 0), (_receiver.Dropped(InputDrop.Address), _receiver.Dropped(InputDrop.Mac), _receiver.Accepted));
        Assert.Empty(Submits);
    }

    [Fact]
    public void An_ipv6_phone_is_matched_by_its_whole_address()
    {
        using var receiver = new InputReceiver(_backend, new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var phone = PhoneSocket(IPAddress.IPv6Loopback);
        receiver.Open(Slot, Key, IPAddress.IPv6Loopback);
        var pad = _backend.Create(PadType.Ds4);
        receiver.Attach(Slot, pad);
        receiver.Open(Slot + 1, Key, IPAddress.Parse("::2"));

        SendAndWait(receiver, phone, Input(sequence: 1, Pressed));
        SendAndWait(receiver, phone, Input(sequence: 1, Pressed, slot: Slot + 1));

        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
        Assert.Equal(1, receiver.Dropped(InputDrop.Address));
        Assert.True(receiver.TryGetSlot(Slot, out var input));
        Assert.Equal(PortOf(phone), input.ReplyPort);
    }

    [Fact]
    public void A_receiver_on_ipv6_also_takes_an_ipv4_phone()
    {
        using var receiver = new InputReceiver(_backend, new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 0));
        receiver.Open(Slot, Key, IPAddress.Loopback);
        var pad = _backend.Create(PadType.Ds4);
        receiver.Attach(Slot, pad);

        SendAndWait(receiver, _phone, Input(sequence: 1, Pressed));

        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
        Assert.True(receiver.TryGetSlot(Slot, out var input));
        Assert.Equal(PortOf(_phone), input.ReplyPort);
    }

    // Without it, a process that binds the PC's own address with SO_REUSEADDR takes every packet from a
    // receiver on 0.0.0.0 (tried on Windows 11 in 2.6). On loopback, Windows refuses that bind anyway,
    // and a test on 0.0.0.0 can raise a firewall prompt, so this reads the option.
    [Fact]
    public void The_receivers_port_is_bound_exclusively()
    {
        Assert.True(_receiver.BindsExclusively);
    }

    [Fact]
    public void A_packet_from_the_phones_ip_on_a_new_port_is_taken_and_moves_the_reply_port()
    {
        var pad = OpenWithPad();
        using var recreated = PhoneSocket();

        Send(Input(sequence: 1, Pressed));
        Send(Input(sequence: 2, Other), recreated);

        Assert.Equal([new PadCall.Submitted(pad, Pressed), new PadCall.Submitted(pad, Other)], Submits);
        Assert.True(_receiver.TryGetSlot(Slot, out var input));
        Assert.Equal(PortOf(recreated), input.ReplyPort);
        Assert.NotEqual(PortOf(_phone), input.ReplyPort);
    }

    [Fact]
    public void An_ipv4_address_in_ipv6_form_matches_its_ipv4_packets()
    {
        var pad = OpenWithPad(phone: IPAddress.Loopback.MapToIPv6());

        Send(Input(sequence: 1, Pressed));

        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
    }

    [Fact]
    public void A_replayed_packet_is_dropped()
    {
        var pad = OpenWithPad();
        var packet = Input(sequence: 7, Pressed);

        Send(packet);
        Send(packet);

        Assert.Equal((1, 1), (_receiver.Accepted, _receiver.Dropped(InputDrop.Stale)));
        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
    }

    [Fact]
    public void An_older_packet_after_a_newer_one_is_dropped()
    {
        var pad = OpenWithPad();

        Send(Input(sequence: 6, Pressed));
        Send(Input(sequence: 5, Other));

        Assert.Equal(1, _receiver.Dropped(InputDrop.Stale));
        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
        Assert.True(_receiver.TryGetSlot(Slot, out var input));
        Assert.Equal(6u, input.Sequence);
    }

    [Fact]
    public void Gaps_are_fine_and_the_newest_state_wins()
    {
        var pad = OpenWithPad();

        Send(Input(sequence: 5, Pressed));
        Send(Input(sequence: 1000, Other));
        Send(Input(sequence: 999, Pressed));

        Assert.Equal((2, 1), (_receiver.Accepted, _receiver.Dropped(InputDrop.Stale)));
        Assert.Equal([new PadCall.Submitted(pad, Pressed), new PadCall.Submitted(pad, Other)], Submits);
    }

    [Fact]
    public void Sequences_wrap_from_the_top_of_the_u32_to_0()
    {
        var pad = OpenWithPad();

        Send(Input(sequence: uint.MaxValue, Pressed));
        Send(Input(sequence: 0, Other));

        Assert.Equal([new PadCall.Submitted(pad, Pressed), new PadCall.Submitted(pad, Other)], Submits);
    }

    // Every join gets a new session key (protocol/SPEC.md, "Join sequence"), so the old session's
    // packets fail the new one's mac however far back the mark starts.
    [Fact]
    public void A_new_session_takes_any_first_sequence()
    {
        var pad = OpenWithPad();
        Send(Input(sequence: 500, Pressed));

        _receiver.Close(Slot);
        _receiver.Open(Slot, OtherKey, IPAddress.Loopback);
        _receiver.Attach(Slot, pad);
        Send(Input(sequence: 501, Pressed));
        Send(Input(sequence: 2, Other, key: OtherKey));

        Assert.Equal(1, _receiver.Dropped(InputDrop.Mac));
        Assert.Equal([new PadCall.Submitted(pad, Pressed), new PadCall.Submitted(pad, Other)], Submits);
    }

    [Fact]
    public void A_packet_for_a_slot_with_no_session_is_dropped_before_its_mac_is_read()
    {
        OpenWithPad();

        Send(Input(sequence: 1, Pressed, slot: Slot + 1));
        Send(Input(sequence: 2, Pressed));

        Assert.Equal((1, 1), (_receiver.Dropped(InputDrop.NoSession), _receiver.Accepted));
        Assert.Equal(0, _receiver.ReceiveErrors);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Each_invalid_vector_is_dropped_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        for (var slot = 0; slot < Packet.SlotCount; slot++)
            _receiver.Open(slot, Key, IPAddress.Loopback);

        Send(Vectors.Hex(vector["hex"]));

        Assert.Equal(1, _receiver.Dropped(Enum.Parse<InputDrop>(vector["reason"]!.GetValue<string>(), ignoreCase: true)));
        Assert.Equal(0, _receiver.Accepted);
    }

    [Fact]
    public void Datagrams_of_any_other_size_are_dropped_for_length()
    {
        OpenWithPad();
        var packet = Input(sequence: 1, Pressed);

        Send([]);
        Send(packet[..4]);
        Send([.. packet, 0]);
        Send(new byte[60_000]);

        Assert.Equal((4, 0), (_receiver.Dropped(InputDrop.Length), _receiver.ReceiveErrors));
    }

    [Fact]
    public void An_unchanged_state_is_taken_but_not_submitted_again()
    {
        var pad = OpenWithPad();

        Send(Input(sequence: 1, Pressed, sentMs: 10));
        Send(Input(sequence: 2, Pressed, sentMs: 20));

        Assert.Equal((2, 1), (_receiver.Accepted, _receiver.Submitted));
        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
        Assert.True(_receiver.TryGetSlot(Slot, out var input));
        Assert.Equal((ushort)20, input.SentMs);
    }

    [Fact]
    public void A_detached_slot_keeps_its_mark_and_a_new_pad_gets_the_next_state()
    {
        var first = OpenWithPad();
        var replay = Input(sequence: 1, Pressed);
        Send(replay);

        _receiver.Detach(Slot);
        Send(Input(sequence: 2, Pressed));
        Send(replay);
        var second = _backend.Create(PadType.X360);
        _receiver.Attach(Slot, second);
        Send(Input(sequence: 3, Pressed));

        Assert.Equal((3, 1), (_receiver.Accepted, _receiver.Dropped(InputDrop.Stale)));
        Assert.Equal([new PadCall.Submitted(first, Pressed), new PadCall.Submitted(second, Pressed)], Submits);
    }

    // As when a slot leaves Grace in the same session (PLAN.md 3.1): the pad was sent neutral meanwhile,
    // so the held state must reach it again.
    [Fact]
    public void The_same_pad_attached_again_gets_the_next_state_unchanged_or_not()
    {
        var pad = OpenWithPad();
        Send(Input(sequence: 1, Pressed));

        _receiver.Detach(Slot);
        _backend.Submit(pad, default);
        _receiver.Attach(Slot, pad);
        Send(Input(sequence: 2, Pressed));

        Assert.Equal([new PadCall.Submitted(pad, Pressed), new PadCall.Submitted(pad, default), new PadCall.Submitted(pad, Pressed)], Submits);
    }

    [Fact]
    public void After_close_the_slots_packets_are_dropped()
    {
        OpenWithPad();
        Send(Input(sequence: 1, Pressed));

        _receiver.Close(Slot);
        Send(Input(sequence: 2, Other));

        Assert.False(_receiver.TryGetSlot(Slot, out _));
        Assert.Equal(1, _receiver.Dropped(InputDrop.NoSession));
        Assert.Single(Submits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detach_and_close_return_only_after_a_submit_under_way_ends(bool close)
    {
        var cancel = TestContext.Current.CancellationToken;
        using var backend = new BlockingBackend();
        using var receiver = new InputReceiver(backend, new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            receiver.Open(Slot, Key, IPAddress.Loopback);
            receiver.Attach(Slot, backend.Create(PadType.Ds4));
            _phone.SendTo(Input(sequence: 1, Pressed), receiver.LocalEndPoint);
            Assert.True(backend.Entered.Wait(Timeout, cancel));

            var stopping = Task.Run(() =>
            {
                if (close)
                    receiver.Close(Slot);
                else
                    receiver.Detach(Slot);
            }, cancel);

            await Task.Delay(300, cancel);
            Assert.False(stopping.IsCompleted);
            backend.Release.Set();
            await stopping.WaitAsync(Timeout, cancel);
        }
        finally
        {
            backend.Release.Set(); // the receiver's Dispose waits for its thread, which may be in Submit
        }
    }

    [Fact]
    public void A_submit_that_throws_is_counted_and_the_receiver_carries_on()
    {
        OpenWithPad();
        _receiver.Detach(Slot);
        _receiver.Attach(Slot, new PadId(int.MaxValue)); // a pad the backend doesn't hold
        Send(Input(sequence: 1, Pressed));

        _receiver.Detach(Slot);
        var pad = _backend.Create(PadType.Ds4);
        _receiver.Attach(Slot, pad);
        Send(Input(sequence: 2, Pressed));

        Assert.Equal((1, 1), (_receiver.SubmitErrors, _receiver.Submitted));
        Assert.Equal([new PadCall.Submitted(pad, Pressed)], Submits);
    }

    [Fact]
    public void Session_calls_refuse_what_they_cannot_do()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _receiver.Open(16, Key, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => _receiver.Open(Slot, Key[..^1], IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => _receiver.Open(Slot, Key, IPAddress.IPv6Loopback));
        Assert.Throws<InvalidOperationException>(() => _receiver.Attach(Slot, new PadId(1)));

        _receiver.Open(Slot, Key, IPAddress.Loopback);
        _receiver.Attach(Slot, new PadId(1));

        Assert.Throws<InvalidOperationException>(() => _receiver.Open(Slot, Key, IPAddress.Loopback));
        Assert.Throws<InvalidOperationException>(() => _receiver.Attach(Slot, new PadId(2)));

        _receiver.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _receiver.Open(Slot + 1, Key, IPAddress.Loopback));
        Assert.Throws<ObjectDisposedException>(() => _receiver.Attach(Slot + 1, new PadId(2)));
    }

    [Fact]
    public void Detach_and_close_do_nothing_for_a_slot_without_a_session()
    {
        _receiver.Detach(Slot);
        _receiver.Close(Slot);
        _receiver.Open(Slot, Key, IPAddress.Loopback);
        _receiver.Detach(Slot);
        _receiver.Close(Slot);
        _receiver.Close(Slot);

        Assert.False(_receiver.TryGetSlot(Slot, out _));
    }

    private IEnumerable<PadCall.Submitted> Submits => _backend.Calls.OfType<PadCall.Submitted>();

    private static Socket PhoneSocket(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address, 0));
        return socket;
    }

    /// <summary>Sends a datagram and waits until the receiver has handled it.</summary>
    private static void SendAndWait(InputReceiver receiver, Socket from, byte[] datagram)
    {
        var expected = receiver.Received + 1;
        var to = receiver.LocalEndPoint;
        from.SendTo(datagram, to.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(to.Address.MapToIPv4(), to.Port) : to);
        Assert.True(SpinWait.SpinUntil(() => receiver.Received >= expected, Timeout), $"handled {receiver.Received} of {expected}");
    }

    private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static byte[] Input(uint sequence, PadState state, int slot = Slot, ushort sentMs = 0, byte[]? key = null)
    {
        var bytes = new byte[InputPacket.Size];
        new InputPacket
        {
            Slot = (byte)slot,
            Sequence = sequence,
            Buttons = state.Buttons,
            Lx = state.Lx,
            Ly = state.Ly,
            Rx = state.Rx,
            Ry = state.Ry,
            Lt = state.Lt,
            Rt = state.Rt,
            SentMs = sentMs,
        }.Write(bytes, key ?? Key);
        return bytes;
    }

    private PadId OpenWithPad(IPAddress? phone = null)
    {
        _receiver.Open(Slot, Key, phone ?? IPAddress.Loopback);
        var pad = _backend.Create(PadType.Ds4);
        _receiver.Attach(Slot, pad);
        return pad;
    }

    private void Send(byte[] datagram, Socket? from = null) => SendAndWait(_receiver, from ?? _phone, datagram);

    /// <summary>A backend whose Submit waits until the test releases it.</summary>
    private sealed class BlockingBackend : IVirtualPadBackend
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public event Action<PadId, PadFeedback>? Feedback
        {
            add { }
            remove { }
        }

        public PadId Create(PadType type) => new(1);

        public void Submit(PadId pad, in PadState state)
        {
            Entered.Set();
            Release.Wait();
        }

        public void Remove(PadId pad)
        {
        }

        public int? ReadUserIndex(PadId pad) => null;

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
