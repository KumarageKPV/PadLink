using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PadLink.Core.Input;
using PadLink.Core.Pads;
using PadLink.Protocol;

namespace PadLink.Core.Tests;

// The hot path (UDP receive → pad submit) must not allocate per packet (CLAUDE.md, Rules). PLAN.md
// 2.6 asks for 16 phones at 120 packets a second for 10 s.
public sealed class InputReceiverLoadTests(ITestOutputHelper output)
{
    private const int Rate = 120;
    private const int Seconds = 10;
    private const int WarmUpTicks = Rate;
    private const int Slots = Packet.SlotCount;

    // Each tick every phone sends, then one forged and one replayed packet follow.
    private const int DatagramsPerTick = Slots + 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Sixteen_phones_at_120_packets_a_second_for_10_s_allocate_nothing_per_packet()
    {
        using var backend = new FakePadBackend { RecordSubmits = false };
        using var receiver = new InputReceiver(backend, new IPEndPoint(IPAddress.Loopback, 0));
        var phones = new Phone[Slots];
        for (var slot = 0; slot < Slots; slot++)
        {
            phones[slot] = new Phone(slot, receiver.LocalEndPoint);
            receiver.Open(slot, phones[slot].Key, IPAddress.Loopback);
            receiver.Attach(slot, phones[slot].Pad = backend.Create(slot % 2 == 0 ? PadType.X360 : PadType.Ds4));
        }

        try
        {
            Run(phones, 0, WarmUpTicks, receiver);
            var allocatedBefore = receiver.ReceiveThreadAllocatedBytes;
            var (receivedBefore, acceptedBefore) = (receiver.Received, receiver.Accepted);
            var clock = Stopwatch.StartNew();

            Run(phones, WarmUpTicks, Rate * Seconds, receiver);
            clock.Stop();
            var allocated = receiver.ReceiveThreadAllocatedBytes - allocatedBefore;
            var received = receiver.Received - receivedBefore;

            output.WriteLine($"{received} datagrams in {clock.Elapsed.TotalSeconds:0.00} s after a {WarmUpTicks}-tick warm-up: " +
                $"{receiver.Accepted - acceptedBefore} accepted, {receiver.Dropped(InputDrop.Mac)} dropped for mac and " +
                $"{receiver.Dropped(InputDrop.Stale)} for sequence in all; {allocated} bytes allocated on the receive thread");
            Assert.Equal(0, allocated);
            Assert.Equal(Rate * Seconds * DatagramsPerTick, received);
            Assert.Equal(Rate * Seconds * Slots, receiver.Accepted - acceptedBefore);
            var totalTicks = WarmUpTicks + Rate * Seconds;
            Assert.Equal((totalTicks, totalTicks), (receiver.Dropped(InputDrop.Mac), receiver.Dropped(InputDrop.Stale)));
            Assert.Equal(0, receiver.SubmitErrors + receiver.ReceiveErrors);
            foreach (var phone in phones)
                Assert.Equal((phone.State, phone.Changes), (backend.StateOf(phone.Pad), backend.SubmitCount(phone.Pad)));
            Assert.Equal(phones.Sum(phone => phone.Changes), receiver.Submitted);
            Assert.InRange(clock.Elapsed.TotalSeconds, Seconds - 0.1, Seconds + 2);
        }
        finally
        {
            foreach (var phone in phones)
                phone.Dispose();
        }
    }

    // Anyone on the Wi-Fi can send these, as fast as they like.
    [Fact]
    public void Every_drop_path_allocates_nothing()
    {
        const int Rounds = 2_000;
        var key = RandomNumberGenerator.GetBytes(32);
        using var backend = new FakePadBackend { RecordSubmits = false };
        using var receiver = new InputReceiver(backend, new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Open(0, key, IPAddress.Loopback);
        receiver.Attach(0, backend.Create(PadType.Ds4));
        receiver.Open(1, key, IPAddress.Parse("127.0.0.2")); // slot 2 has no session
        sender.SendTo(Datagram(0, 1_000_000, key), receiver.LocalEndPoint); // the mark the stale packets fall behind
        Assert.True(SpinWait.SpinUntil(() => receiver.Received == 1, Timeout));

        var good = Datagram(0, 1_000_001, key);
        (InputDrop Reason, byte[] Datagram)[] drops =
        [
            (InputDrop.Length, good[..^1]),
            (InputDrop.Length, new byte[2_000]),
            (InputDrop.Slot, With(good, 3, 16)),
            (InputDrop.NoSession, Datagram(2, 1, key)),
            (InputDrop.Address, Datagram(1, 1, key)),
            (InputDrop.Magic, With(good, 0, 0x51)),
            (InputDrop.Version, With(good, 1, 2)),
            (InputDrop.Type, With(good, 2, 2)),
            (InputDrop.Mac, With(good, InputPacket.Size - 1, (byte)(good[^1] ^ 1))),
            (InputDrop.Stale, Datagram(0, 5, key)),
        ];
        SendRounds(Rounds / 10); // warm-up
        var before = receiver.ReceiveThreadAllocatedBytes;

        SendRounds(Rounds);
        var allocated = receiver.ReceiveThreadAllocatedBytes - before;

        output.WriteLine($"{Rounds * drops.Length} dropped datagrams after warm-up; {allocated} bytes allocated on the receive thread");
        Assert.Equal(0, allocated);
        foreach (var reason in drops.Select(drop => drop.Reason).Distinct())
            Assert.Equal(Rounds * 11 / 10 * drops.Count(drop => drop.Reason == reason), receiver.Dropped(reason));
        Assert.Equal(1, receiver.Accepted);

        // A round at a time, so the socket's buffer can't overflow and lose any.
        void SendRounds(int rounds)
        {
            for (var round = 0; round < rounds; round++)
            {
                var expected = receiver.Received + drops.Length;
                foreach (var (_, datagram) in drops)
                    sender.SendTo(datagram, receiver.LocalEndPoint);
                Assert.True(SpinWait.SpinUntil(() => receiver.Received >= expected, Timeout), $"handled {receiver.Received} of {expected}");
            }
        }
    }

    private static byte[] Datagram(int slot, uint sequence, byte[] key)
    {
        var bytes = new byte[InputPacket.Size];
        new InputPacket { Slot = (byte)slot, Sequence = sequence, Lx = 5 }.Write(bytes, key);
        return bytes;
    }

    private static byte[] With(byte[] bytes, int offset, byte value)
    {
        var changed = (byte[])bytes.Clone();
        changed[offset] = value;
        return changed;
    }

    /// <summary>Sends ticks first to last − 1 at <see cref="Rate"/> a second, then waits until the receiver has handled them.</summary>
    private static void Run(Phone[] phones, int first, int count, InputReceiver receiver)
    {
        var expected = receiver.Received + (long)count * DatagramsPerTick;
        var clock = Stopwatch.StartNew();
        for (var tick = first; tick < first + count; tick++)
        {
            var due = TimeSpan.FromSeconds((tick - first) / (double)Rate);
            while (clock.Elapsed < due)
                Thread.Sleep(1);
            foreach (var phone in phones)
                phone.SendNext(tick);
            phones[tick % Slots].SendForged();
            phones[(tick + 5) % Slots].SendLastAgain();
        }
        Assert.True(SpinWait.SpinUntil(() => receiver.Received >= expected, Timeout), $"handled {receiver.Received} of {expected}");
    }

    /// <summary>A phone that moves its sticks most ticks and repeats its state on every third, as a keepalive does.</summary>
    private sealed class Phone : IDisposable
    {
        private readonly int _slot;
        private readonly EndPoint _pc;
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly byte[] _last = new byte[InputPacket.Size];
        private readonly byte[] _forged = new byte[InputPacket.Size];
        private uint _sequence;
        private bool _sent;

        public Phone(int slot, EndPoint pc)
        {
            _slot = slot;
            _pc = pc;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _sequence = uint.MaxValue - (uint)slot * 100; // near the top, so most phones wrap during the run
        }

        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public PadId Pad { get; set; }
        public PadState State { get; private set; }

        /// <summary>The submits the receiver should make: the first state, then each change.</summary>
        public long Changes { get; private set; }

        public void SendNext(int tick)
        {
            var state = tick % 3 == 0 && _sent
                ? State
                : new PadState
                {
                    Buttons = (tick / 10 % 2 == 0) ? PadButtons.FaceBottom : PadButtons.None,
                    Lx = (short)(tick * 37 + _slot),
                    Ly = (short)-(tick * 11),
                    Rt = (byte)tick,
                };
            if (!_sent || state != State)
                Changes++;
            State = state;
            _sent = true;
            Write(_last, ++_sequence, state, Key);
            _socket.SendTo(_last, _pc);
        }

        /// <summary>A packet far ahead in sequence with a flipped mac bit: dropped, and the mark stays.</summary>
        public void SendForged()
        {
            Write(_forged, _sequence + 1000, default, Key);
            _forged[^1] ^= 1;
            _socket.SendTo(_forged, _pc);
        }

        public void SendLastAgain() => _socket.SendTo(_last, _pc);

        public void Dispose() => _socket.Dispose();

        private void Write(byte[] destination, uint sequence, PadState state, byte[] key) => new InputPacket
        {
            Slot = (byte)_slot,
            Sequence = sequence,
            Buttons = state.Buttons,
            Lx = state.Lx,
            Ly = state.Ly,
            Rx = state.Rx,
            Ry = state.Ry,
            Lt = state.Lt,
            Rt = state.Rt,
            SentMs = (ushort)Environment.TickCount64,
        }.Write(destination, key);
    }
}
