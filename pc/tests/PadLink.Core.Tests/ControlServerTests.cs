using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PadLink.Core.Control;
using PadLink.Protocol.Control;

namespace PadLink.Core.Tests;

// Real phones' frames over loopback TCP; the clock is fake, so pings and the quiet timeout happen only
// when a test moves it.
public sealed class ControlServerTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly PairingCode _code;
    private readonly MemoryPairingStore _store = new();
    private readonly RecordingJoinHandler _handler = new();
    private readonly ControlServer _server;
    private readonly List<PhoneClient> _clients = [];
    private readonly List<Exception> _failures = [];

    public ControlServerTests()
    {
        _code = new PairingCode(_time);
        _code.Show();
        _server = new ControlServer(_handler, _code, _store, new IPEndPoint(IPAddress.Loopback, 0), _time);
        _server.ConnectionFailed += e =>
        {
            lock (_failures)
                _failures.Add(e);
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Phones first: a closing connection waits for its phone to close too, up to a second.
        foreach (var client in _clients)
            client.Dispose();
        await _server.DisposeAsync();
        _code.Dispose();
        lock (_failures)
            Assert.Empty(_failures);
    }

    [Fact]
    public void A_new_phone_pairs_and_joins_over_tcp()
    {
        var phone = new PhoneSide();
        using var client = Connect();

        client.Send(phone.Hello());
        var request = Assert.IsType<PairRequired>(client.Receive());
        client.Send(phone.Pair(request, _code.Current!));
        var welcome = Assert.IsType<Welcome>(client.Receive());

        Assert.Equal(0, welcome.Slot);
        var (joined, sessionKey) = Assert.Single(_handler.Joins);
        Assert.Equal(IPAddress.Loopback, joined.Address);
        Assert.Equal(phone.SessionKey, sessionKey);
        Assert.Equal(phone.PairingKey, _store.KeyOf(phone.DeviceId));
    }

    [Fact]
    public void A_known_phone_authenticates_and_joins_over_tcp()
    {
        var phone = PhoneSide.Paired(_store);
        using var client = Connect();

        var welcome = JoinKnown(client, phone);

        Assert.Equal(0, welcome.Slot);
        Assert.Equal([$"join {phone.DeviceId} 0"], _handler.Calls);
    }

    [Fact]
    public void A_frame_sent_a_byte_at_a_time_is_read_whole()
    {
        using var client = Connect();

        foreach (var b in ControlFrame.Encode(new Ping()))
        {
            client.SendRaw([b]);
            Thread.Sleep(5);
        }

        Assert.IsType<Pong>(client.Receive());
    }

    [Fact]
    public void Two_frames_in_one_write_are_both_read()
    {
        using var client = Connect();

        client.SendRaw([.. ControlFrame.Encode(new Ping()), .. ControlFrame.Encode(new Ping())]);

        Assert.IsType<Pong>(client.Receive());
        Assert.IsType<Pong>(client.Receive());
    }

    [Fact]
    public void A_4096_byte_frame_is_read()
    {
        var phone = new PhoneSide();
        var shortHello = Bodies.Of(phone.Hello()).Length;
        var hello = phone.Hello() with { DeviceName = PhoneSide.Name + new string('x', ControlFrame.MaxBodySize - shortHello) };
        Assert.Equal(ControlFrame.MaxBodySize, Bodies.Of(hello).Length);
        using var client = Connect();

        client.Send(hello);

        Assert.IsType<PairRequired>(client.Receive());
    }

    [Theory]
    [InlineData((uint)ControlFrame.MaxBodySize + 1)]
    [InlineData(uint.MaxValue)]
    [InlineData(0u)]
    public void A_length_of_0_or_over_4096_closes_the_connection_without_waiting_for_a_body(uint length)
    {
        using var client = Connect();
        var header = new byte[ControlFrame.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);

        // Only the header: a server that waited for the body would never close.
        client.SendRaw(header);

        client.AssertClosed();
        WaitUntilNoConnections();
    }

    [Fact]
    public void A_bad_frame_gets_bad_message_and_then_the_connection_closes()
    {
        using var client = Connect();
        var frame = BadJsonFrame();

        client.SendRaw(frame);

        Assert.Equal(new Error(ErrorCodes.BadMessage), client.Receive());
        client.AssertClosed();
    }

    // The ping after the bad frame is left unread. Closing a socket with unread bytes resets it, and a
    // reset may throw away the error before the phone reads it, so the server reads to the end first.
    [Fact]
    public void A_bad_frame_followed_by_more_bytes_still_gets_its_error()
    {
        using var client = Connect();
        var frame = BadJsonFrame();

        client.SendRaw([.. frame, .. ControlFrame.Encode(new Ping()), .. ControlFrame.Encode(new Ping())]);

        Assert.Equal(new Error(ErrorCodes.BadMessage), client.Receive());
        client.AssertClosed();
    }

    [Fact]
    public void A_phone_that_never_reads_is_cut_off_once_its_replies_back_up()
    {
        using var client = Connect();
        RoundTrip(client);
        var pings = Enumerable.Repeat(ControlFrame.Encode(new Ping()), 1000).SelectMany(frame => frame).ToArray();

        var sent = 0L;
        try
        {
            // Far more pongs than the TCP buffers on both sides hold.
            while (sent < 64_000_000)
            {
                client.SendRaw(pings);
                sent += pings.Length;
            }
        }
        catch (SocketException)
        {
            // The server aborted the connection.
        }

        WaitUntilNoConnections();
        Assert.True(sent < 64_000_000, "the server kept the connection open");
    }

    [Fact]
    public void A_wrong_code_gets_bad_proof_and_then_the_connection_closes()
    {
        var phone = new PhoneSide();
        using var client = Connect();
        client.Send(phone.Hello());
        var request = Assert.IsType<PairRequired>(client.Receive());

        client.Send(phone.Pair(request, _code.Current == "000000" ? "000001" : "000000"));

        Assert.Equal(new Error(ErrorCodes.BadProof), client.Receive());
        client.AssertClosed();
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task A_link_dropped_after_welcome_leaves_once()
    {
        var phone = PhoneSide.Paired(_store);
        var client = Connect();
        JoinKnown(client, phone);

        client.Dispose();

        _handler.WaitUntil(calls => calls.Contains($"leave {phone.DeviceId}"));
        WaitUntilNoConnections();
        await _server.DisposeAsync();
        Assert.Equal([$"join {phone.DeviceId} 0", $"leave {phone.DeviceId}"], _handler.Calls);
        Assert.Same(_handler.Joins[0].Phone, Assert.Single(_handler.Leaves));
    }

    [Fact]
    public void A_link_dropped_before_welcome_neither_joins_nor_leaves()
    {
        var client = Connect();
        client.Send(new PhoneSide().Hello());
        Assert.IsType<PairRequired>(client.Receive());

        client.Dispose();

        WaitUntilNoConnections();
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public void Bye_after_welcome_leaves_and_closes()
    {
        var phone = PhoneSide.Paired(_store);
        using var client = Connect();
        JoinKnown(client, phone);

        client.Send(new Bye());

        client.AssertClosed();
        _handler.WaitUntil(calls => calls.Contains($"leave {phone.DeviceId}"));
    }

    [Fact]
    public void The_pc_pings_every_second_and_answers_pings()
    {
        using var client = Connect();
        RoundTrip(client);

        _time.Advance(ControlServer.PingInterval);
        Assert.IsType<Ping>(client.Receive());
        _time.Advance(ControlServer.PingInterval);
        Assert.IsType<Ping>(client.Receive());
        client.Send(new Ping());

        Assert.IsType<Pong>(client.Receive());
        Assert.True(client.IsOpen());
    }

    [Fact]
    public void A_joined_phone_that_goes_quiet_for_5_seconds_is_closed_and_leaves()
    {
        var phone = PhoneSide.Paired(_store);
        using var client = Connect();
        JoinKnown(client, phone);

        _time.Advance(ControlServer.QuietTimeout - TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 4; i++)
            Assert.IsType<Ping>(client.Receive());
        Assert.True(client.IsOpen());
        Assert.DoesNotContain($"leave {phone.DeviceId}", _handler.Calls);

        _time.Advance(TimeSpan.FromMilliseconds(100));

        client.AssertClosed(skipPings: true);
        _handler.WaitUntil(calls => calls.Contains($"leave {phone.DeviceId}"));
    }

    [Fact]
    public void Any_frame_from_the_phone_restarts_the_quiet_timeout()
    {
        using var client = Connect();
        RoundTrip(client);

        _time.Advance(TimeSpan.FromSeconds(4));
        // A pong alone, not a ping: any frame counts.
        var read = _server.FramesRead;
        client.Send(new Pong());
        Assert.True(SpinWait.SpinUntil(() => _server.FramesRead > read, TimeSpan.FromSeconds(5)), "the pong was never read");
        _time.Advance(TimeSpan.FromSeconds(4));
        for (var i = 0; i < 8; i++)
            Assert.IsType<Ping>(client.Receive());
        Assert.True(client.IsOpen());

        _time.Advance(TimeSpan.FromSeconds(1));

        client.AssertClosed(skipPings: true);
    }

    [Fact]
    public void A_connection_that_never_joins_is_closed_once_quiet()
    {
        using var client = Connect();
        client.Send(new PhoneSide().Hello());
        Assert.IsType<PairRequired>(client.Receive());

        _time.Advance(ControlServer.QuietTimeout);

        client.AssertClosed(skipPings: true);
        WaitUntilNoConnections();
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public void A_hello_or_a_failed_auth_with_a_joined_device_id_leaves_the_joined_phone_alone()
    {
        var phone = PhoneSide.Paired(_store);
        using var first = Connect();
        JoinKnown(first, phone);
        using var second = Connect();
        var impostor = new PhoneSide(phone.DeviceId);

        second.Send(impostor.Hello());
        Assert.IsType<Challenge>(second.Receive());
        RoundTrip(first);
        second.Send(new Auth(new byte[ControlMessage.HmacSize]));

        Assert.Equal(new Error(ErrorCodes.BadAuth), second.Receive());
        second.AssertClosed();
        RoundTrip(first);
        Assert.Equal([$"join {phone.DeviceId} 0"], _handler.Calls);
    }

    [Fact]
    public void A_new_connection_that_passes_auth_takes_the_slot_over_and_the_old_one_gets_bye()
    {
        var phone = PhoneSide.Paired(_store);
        using var first = Connect();
        JoinKnown(first, phone);
        using var second = Connect();

        var welcome = JoinKnown(second, phone.Reconnect());

        Assert.Equal(0, welcome.Slot);
        Assert.IsType<Bye>(first.Receive());
        first.AssertClosed();
        _handler.WaitUntil(calls => calls.Length == 3);
        Assert.Equal([$"join {phone.DeviceId} 0", $"join {phone.DeviceId} 0", $"leave {phone.DeviceId}"], _handler.Calls);
        Assert.Same(_handler.Joins[0].Phone, Assert.Single(_handler.Leaves));
        RoundTrip(second);
    }

    [Fact]
    public async Task Disposing_the_server_says_bye_to_every_phone_and_each_leaves()
    {
        var ana = PhoneSide.Paired(_store, "ana");
        var ben = PhoneSide.Paired(_store, "ben");
        using var first = Connect();
        using var second = Connect();
        JoinKnown(first, ana);
        JoinKnown(second, ben);
        using var waiting = Connect();
        RoundTrip(waiting);

        await _server.DisposeAsync();

        Assert.IsType<Bye>(first.Receive());
        first.AssertClosed();
        Assert.IsType<Bye>(second.Receive());
        second.AssertClosed();
        Assert.IsType<Bye>(waiting.Receive());
        waiting.AssertClosed();
        Assert.Equal(["join ana 0", "join ben 1", "leave ana", "leave ben"], _handler.Calls.Order());
        Assert.Equal(0, _server.OpenConnections);
    }

    [Fact]
    public void A_handler_that_throws_closes_only_that_connection()
    {
        var handler = new RecordingJoinHandler { FailFor = "broken" };
        using var server = new ControlServer(handler, _code, _store, new IPEndPoint(IPAddress.Loopback, 0), _time);
        Exception? failure = null;
        server.ConnectionFailed += e => failure = e;
        var broken = PhoneSide.Paired(_store, "broken");
        var fine = PhoneSide.Paired(_store, "fine");
        using var first = new PhoneClient(server.LocalEndPoint);
        using var second = new PhoneClient(server.LocalEndPoint);

        first.Send(broken.Hello());
        first.Send(broken.Auth(Assert.IsType<Challenge>(first.Receive())));
        first.AssertClosed();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref failure) is not null, TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, JoinKnown(second, fine).Slot);
    }

    [Fact]
    public void A_connection_failed_handler_that_throws_does_not_stop_the_teardown()
    {
        var handler = new RecordingJoinHandler { FailFor = "broken" };
        using var server = new ControlServer(handler, _code, _store, new IPEndPoint(IPAddress.Loopback, 0), _time);
        server.ConnectionFailed += _ => throw new InvalidOperationException("a subscriber failed too");
        var broken = PhoneSide.Paired(_store, "broken");
        using var client = new PhoneClient(server.LocalEndPoint);

        client.Send(broken.Hello());
        client.Send(broken.Auth(Assert.IsType<Challenge>(client.Receive())));

        client.AssertClosed();
        Assert.True(SpinWait.SpinUntil(() => server.OpenConnections == 0, TimeSpan.FromSeconds(5)), "the connection was never torn down");
    }

    [Fact]
    public void The_server_takes_IPv4_endpoints_only()
    {
        Assert.Throws<ArgumentException>(() =>
            new ControlServer(_handler, _code, _store, new IPEndPoint(IPAddress.IPv6Loopback, 0), _time));
    }

    private static Welcome JoinKnown(PhoneClient client, PhoneSide phone)
    {
        client.Send(phone.Hello());
        client.Send(phone.Auth(Assert.IsType<Challenge>(client.Receive())));
        return Assert.IsType<Welcome>(client.Receive());
    }

    // A ping and its pong: the server has handled every frame sent before it.
    private static void RoundTrip(PhoneClient client, bool skipPings = false)
    {
        client.Send(new Ping());
        Assert.IsType<Pong>(skipPings ? client.ReceiveSkippingPings() : client.Receive());
    }

    // A frame whose body isn't JSON.
    private static byte[] BadJsonFrame()
    {
        var body = "{\"type\":"u8.ToArray();
        var frame = new byte[ControlFrame.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, ControlFrame.HeaderSize);
        return frame;
    }

    private PhoneClient Connect()
    {
        var client = new PhoneClient(_server.LocalEndPoint);
        _clients.Add(client);
        return client;
    }

    private void WaitUntilNoConnections() =>
        Assert.True(SpinWait.SpinUntil(() => _server.OpenConnections == 0, TimeSpan.FromSeconds(5)), "a connection stayed open");
}

/// <summary>A phone's control connection: frames out, decoded messages in, with a 5 s timeout on every read.</summary>
internal sealed class PhoneClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public PhoneClient(IPEndPoint server)
    {
        _socket.ReceiveTimeout = (int)Timeout.TotalMilliseconds;
        _socket.NoDelay = true;
        _socket.Connect(server);
    }

    public void Send(ControlMessage message) => SendRaw(ControlFrame.Encode(message));

    public void SendRaw(byte[] bytes) => _socket.Send(bytes);

    /// <summary>The next message; fails on a close, a bad frame or the timeout.</summary>
    public ControlMessage Receive() => TryReceive() ?? throw new Xunit.Sdk.XunitException("The server closed the connection.");

    public ControlMessage ReceiveSkippingPings()
    {
        while (true)
        {
            var message = Receive();
            if (message is not Ping)
                return message;
        }
    }

    /// <summary>Whether the connection is open: nothing to read for a moment, and no close.</summary>
    public bool IsOpen() => !_socket.Poll(TimeSpan.FromMilliseconds(200), SelectMode.SelectRead);

    /// <summary>Waits for the server to close the connection, with no message before the close.</summary>
    public void AssertClosed(bool skipPings = false)
    {
        while (TryReceive() is { } message)
        {
            if (!(skipPings && message is Ping))
                Assert.Fail($"Expected a close, got {message}.");
        }
    }

    public void Dispose() => _socket.Dispose();

    // Null at a close or a reset.
    private ControlMessage? TryReceive()
    {
        var header = new byte[ControlFrame.HeaderSize];
        if (!TryReadExactly(header))
            return null;
        Assert.Equal(FrameStatus.Incomplete, ControlFrame.TryRead(header, out _));
        var frame = new byte[ControlFrame.HeaderSize + BinaryPrimitives.ReadUInt32LittleEndian(header)];
        header.CopyTo(frame, 0);
        Assert.True(TryReadExactly(frame.AsSpan(ControlFrame.HeaderSize)), "closed inside a frame");
        Assert.Equal(FrameStatus.Complete, ControlFrame.TryRead(frame, out var body));
        Assert.True(ControlFrame.TryDecode(body, out var message, out var fault), $"bad frame from the server: {fault}, {Encoding.UTF8.GetString(body)}");
        return message!;
    }

    private bool TryReadExactly(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int count;
            try
            {
                count = _socket.Receive(buffer[read..]);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                return false;
            }
            if (count == 0)
            {
                Assert.True(read == 0, "closed inside a frame");
                return false;
            }
            read += count;
        }
        return true;
    }
}
