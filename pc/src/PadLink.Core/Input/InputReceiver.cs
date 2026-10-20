using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PadLink.Core.Pads;
using PadLink.Protocol;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Input;

/// <summary>
/// Receives the phones' input packets on one UDP socket and submits each slot's state to its pad
/// (docs/design.md, "Networking protocol", Rules; protocol/SPEC.md, "Packet rules"). The receive thread
/// is the hot path: it allocates nothing per packet, logs nothing and takes no locks.
/// </summary>
/// <remarks>
/// A slot takes packets once <see cref="Open"/> gives it a session, and submits them once
/// <see cref="Attach"/> gives it a pad; the receive thread is then that pad's one writer. Calls for one
/// slot must not overlap, but calls for different slots may. <see cref="Detach"/> and
/// <see cref="Close"/> wait for the receive thread, so a backend must not call them from Submit.
/// </remarks>
public sealed class InputReceiver : IDisposable
{
    // Any UDP datagram fits, so an oversized one is read whole and dropped for its length, rather than
    // failing the receive with an exception, which allocates.
    private const int BufferSize = 65_536;

    // protocol/SPEC.md, "Input packet". The slot picks the session key, so it is read before the packet is.
    private const int SlotOffset = 3;

    private const int NoSlot = -1;
    private const long NoPad = long.MinValue;

    // Windows turns an ICMP port-unreachable for an earlier send into a failed receive. Feedback goes
    // out from this socket, so that is turned off.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly IVirtualPadBackend _backend;
    private readonly Socket _socket;
    private readonly Thread _thread;
    private readonly Session?[] _sessions = new Session?[Packet.SlotCount];
    private readonly long[] _dropped = new long[Enum.GetValues<InputDrop>().Length];
    private int _busySlot = NoSlot; // the slot whose packet the receive thread is handling
    private long _received;
    private long _accepted;
    private long _submitted;
    private long _submitErrors;
    private long _receiveErrors;
    private long _allocatedBytes;
    private volatile bool _disposed;

    /// <summary>
    /// Binds the socket and starts the receive thread. Port 0 picks a free port. An IPv6 endpoint takes
    /// IPv4 packets too, in IPv6 form.
    /// </summary>
    public InputReceiver(IVirtualPadBackend backend, IPEndPoint localEndPoint)
    {
        _backend = backend;
        _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                // Otherwise a process that binds the PC's own address with SO_REUSEADDR takes the
                // packets from a socket on 0.0.0.0.
                _socket.ExclusiveAddressUse = true;
            }
            if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                _socket.DualMode = true;
            _socket.Bind(localEndPoint);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _thread = new Thread(Run) { Name = "PadLink input", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Datagrams handled so far, whatever became of them. Each counts once it is fully handled.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Packets that passed every check and moved their slot's sequence mark.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>States sent to a pad: accepted packets whose state changed, and the first one each pad gets.</summary>
    public long Submitted => Interlocked.Read(ref _submitted);

    /// <summary>Submits that threw. The next packet accepted for the pad is submitted whatever its state.</summary>
    public long SubmitErrors => Interlocked.Read(ref _submitErrors);

    /// <summary>Receives that failed other than at <see cref="Dispose"/>.</summary>
    public long ReceiveErrors => Interlocked.Read(ref _receiveErrors);

    /// <summary>
    /// What the receive thread has allocated since it started, as it read after its latest datagram,
    /// for the allocation test.
    /// </summary>
    internal long ReceiveThreadAllocatedBytes => Volatile.Read(ref _allocatedBytes);

    internal bool BindsExclusively => _socket.ExclusiveAddressUse;

    public long Dropped(InputDrop reason) => Interlocked.Read(ref _dropped[(int)reason]);

    /// <summary>
    /// Starts a session for the slot. It takes packets made with the session key from the phone's IP,
    /// the IP of its control connection, on any port (protocol/SPEC.md, "Packet rules"). Its first
    /// packet may have any sequence, so the key must be the new one every join derives: a key used
    /// before would take that session's packets again. Throws <see cref="InvalidOperationException"/>
    /// if the slot has a session already.
    /// </summary>
    public void Open(int slot, ReadOnlySpan<byte> sessionKey, IPAddress phone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckSlot(slot);
        if (sessionKey.Length != JoinKeys.KeySize)
            throw new ArgumentException($"A session key is {JoinKeys.KeySize} bytes.", nameof(sessionKey));
        phone = InSocketForm(phone);
        if (phone.AddressFamily != _socket.AddressFamily)
            throw new ArgumentException($"The socket takes only {_socket.AddressFamily} packets.", nameof(phone));
        // Pinned, so the GC leaves no copies of the key that Close can't zero.
        var key = GC.AllocateUninitializedArray<byte>(JoinKeys.KeySize, pinned: true);
        sessionKey.CopyTo(key);
        var session = new Session(key, phone);
        if (Interlocked.CompareExchange(ref _sessions[slot], session, null) is not null)
        {
            CryptographicOperations.ZeroMemory(session.Key);
            throw new InvalidOperationException($"Slot {slot} has a session already.");
        }
    }

    /// <summary>
    /// Gives the slot's session a pad. The next packet accepted is submitted to it, even if its state
    /// hasn't changed. Throws <see cref="InvalidOperationException"/> for a slot without a session, or
    /// with a pad already.
    /// </summary>
    public void Attach(int slot, PadId pad)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckSlot(slot);
        var session = Volatile.Read(ref _sessions[slot]) ?? throw new InvalidOperationException($"Slot {slot} has no session.");
        if (Interlocked.CompareExchange(ref session.Pad, pad.Value, NoPad) != NoPad)
            throw new InvalidOperationException($"Slot {slot} has a pad already.");
    }

    /// <summary>
    /// Takes the slot's pad away and returns once no Submit to it is under way, so the caller may remove
    /// it. The session stays: its packets still move the sequence mark, but reach no pad. Does nothing
    /// for a slot without a session or a pad.
    /// </summary>
    public void Detach(int slot)
    {
        CheckSlot(slot);
        var session = Volatile.Read(ref _sessions[slot]);
        if (session is not null && Interlocked.Exchange(ref session.Pad, NoPad) != NoPad)
        {
            WaitUntilIdle(slot);
            // The receive thread reads this only once a pad is attached again. That pad, even the same
            // one, may show another state by then, so the next packet is submitted whatever its state.
            session.SubmittedTo = NoPad;
        }
    }

    /// <summary>
    /// Ends the slot's session, and with it the pad's, returning once no packet for the slot is being
    /// handled. Later packets are dropped for <see cref="InputDrop.NoSession"/>. Does nothing for a slot
    /// without a session.
    /// </summary>
    public void Close(int slot)
    {
        CheckSlot(slot);
        var session = Interlocked.Exchange(ref _sessions[slot], null);
        if (session is null)
            return;
        WaitUntilIdle(slot);
        CryptographicOperations.ZeroMemory(session.Key);
    }

    /// <summary>
    /// What the slot's session has accepted so far, or false for a slot without a session. While
    /// packets arrive, the fields may come from different packets.
    /// </summary>
    public bool TryGetSlot(int slot, out SlotInput input)
    {
        CheckSlot(slot);
        var session = Volatile.Read(ref _sessions[slot]);
        input = session is null ? default : new SlotInput(session.LastSequence, session.LastSentMs, session.ReplyPort);
        return session is not null;
    }

    /// <summary>Stops the receive thread and closes the socket. No Submit runs after it returns; the pads stay the caller's.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _socket.Dispose(); // ends a receive that is waiting
        _thread.Join();
        foreach (var session in _sessions)
        {
            if (session is not null)
                CryptographicOperations.ZeroMemory(session.Key);
        }
    }

    // The form the socket gives a phone's address in: IPv4, or IPv6 with an IPv4 phone mapped into it.
    private IPAddress InSocketForm(IPAddress phone)
    {
        if (_socket.AddressFamily == AddressFamily.InterNetworkV6)
            return phone.MapToIPv6();
        return phone.IsIPv4MappedToIPv6 ? phone.MapToIPv4() : phone;
    }

    private static void CheckSlot(int slot) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)Packet.SlotCount, nameof(slot));

    private static InputDrop DropFor(PacketError error) => error switch
    {
        PacketError.Length => InputDrop.Length,
        PacketError.Magic => InputDrop.Magic,
        PacketError.Version => InputDrop.Version,
        PacketError.Type => InputDrop.Type,
        PacketError.Slot => InputDrop.Slot,
        PacketError.Mac => InputDrop.Mac,
        _ => throw new UnreachableException(),
    };

    private void Run()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var from = new SocketAddress(_socket.AddressFamily);
        try
        {
            while (true)
            {
                int length;
                try
                {
                    length = _socket.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, from);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    if (_disposed)
                        return;
                    Interlocked.Increment(ref _receiveErrors);
                    continue;
                }
                Handle(buffer.AsSpan(0, length), from);
                Volatile.Write(ref _allocatedBytes, GC.GetAllocatedBytesForCurrentThread());
                Interlocked.Increment(ref _received);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Handle(ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (datagram.Length != InputPacket.Size)
        {
            Drop(InputDrop.Length);
            return;
        }
        int slot = datagram[SlotOffset];
        if (slot >= Packet.SlotCount)
        {
            Drop(InputDrop.Slot);
            return;
        }
        // The mark goes up before the session and pad are read, and Detach and Close change those
        // before they read the mark. Both sides use a full fence, so either the receive thread sees the
        // change or the caller sees the mark and waits until this packet is done.
        Interlocked.Exchange(ref _busySlot, slot);
        try
        {
            Handle(datagram, from, Volatile.Read(ref _sessions[slot]));
        }
        finally
        {
            Volatile.Write(ref _busySlot, NoSlot);
        }
    }

    private void Handle(ReadOnlySpan<byte> datagram, SocketAddress from, Session? session)
    {
        // Checked before TryRead, which throws without a session key.
        if (session is null)
        {
            Drop(InputDrop.NoSession);
            return;
        }
        if (!session.IsFrom(from))
        {
            Drop(InputDrop.Address);
            return;
        }
        if (!InputPacket.TryRead(datagram, session.Key, out var packet, out var error))
        {
            Drop(DropFor(error));
            return;
        }
        if (!InputPacket.IsNewer(packet.Sequence, session.LastSequence))
        {
            Drop(InputDrop.Stale);
            return;
        }
        session.Accept(packet, from);
        Interlocked.Increment(ref _accepted);
        Submit(session, PadState.From(packet));
    }

    private void Submit(Session session, in PadState state)
    {
        var pad = Volatile.Read(ref session.Pad);
        if (pad == NoPad || (pad == session.SubmittedTo && state == session.LastSubmitted))
            return;
        try
        {
            _backend.Submit(new PadId((int)pad), state);
        }
        catch (Exception)
        {
            // A pad removed before Detach, or a driver failure. The loop must go on for the other slots.
            session.SubmittedTo = NoPad;
            Interlocked.Increment(ref _submitErrors);
            return;
        }
        session.SubmittedTo = pad;
        session.LastSubmitted = state;
        Interlocked.Increment(ref _submitted);
    }

    private void Drop(InputDrop reason) => Interlocked.Increment(ref _dropped[(int)reason]);

    /// <summary>Waits until the receive thread isn't handling a packet for the slot.</summary>
    private void WaitUntilIdle(int slot)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref _busySlot) == slot)
            spin.SpinOnce();
    }

    private sealed class Session(byte[] key, IPAddress phone)
    {
        // Offsets in a sockaddr_in and a sockaddr_in6, which SocketAddress holds.
        private const int PortOffset = 2;
        private const int IPv4Offset = 4;
        private const int IPv6Offset = 8;

        private readonly AddressFamily _family = phone.AddressFamily;
        private readonly byte[] _ip = phone.GetAddressBytes();

        public byte[] Key { get; } = key;

        // A PadId's value, or NoPad. Attach and Detach set it; the receive thread reads it.
        public long Pad = NoPad;

        // The rest belongs to the receive thread, except that Detach resets SubmittedTo once the thread
        // is done with the pad.
        public uint? LastSequence;
        public ushort LastSentMs;
        public int ReplyPort;
        public long SubmittedTo = NoPad; // the pad LastSubmitted went to
        public PadState LastSubmitted;

        public bool IsFrom(SocketAddress from) =>
            from.Family == _family &&
            from.Buffer.Span.Slice(_family == AddressFamily.InterNetwork ? IPv4Offset : IPv6Offset, _ip.Length).SequenceEqual(_ip);

        public void Accept(in InputPacket packet, SocketAddress from)
        {
            LastSequence = packet.Sequence;
            LastSentMs = packet.SentMs;
            ReplyPort = BinaryPrimitives.ReadUInt16BigEndian(from.Buffer.Span[PortOffset..]);
        }
    }
}

/// <summary>Why <see cref="InputReceiver"/> dropped a datagram, in the order it checks.</summary>
public enum InputDrop
{
    Length,
    Slot,

    /// <summary>The slot has no session, so there is no key to check the mac with.</summary>
    NoSession,

    /// <summary>The datagram didn't come from the IP of the slot's phone.</summary>
    Address,

    Magic,
    Version,
    Type,
    Mac,

    /// <summary>The sequence isn't newer than the last one the slot accepted in its session.</summary>
    Stale,
}

/// <summary>
/// What a slot's session has accepted: the newest packet's sequence, its sentMs, which feedback echoes
/// for round-trip time, and its source port, where feedback goes. Null and 0 before the first packet.
/// </summary>
public readonly record struct SlotInput(uint? Sequence, ushort SentMs, int ReplyPort);
