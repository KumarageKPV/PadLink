using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PadLink.Protocol.Control;

namespace PadLink.Core.Control;

/// <summary>
/// The TCP control server: one connection per phone, framed JSON, the join, pings, and takeover of a slot
/// by a newer connection from the same phone (docs/design.md, "Networking protocol"; protocol/SPEC.md,
/// "Join sequence", "Join rules" and "Control messages"). This is not the hot path: the input packets go
/// to <see cref="Input.InputReceiver"/>.
/// </summary>
public sealed class ControlServer : IAsyncDisposable, IDisposable
{
    /// <summary>Each side pings every second (protocol/SPEC.md, "Control messages").</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    /// <summary>The PC closes a connection that sends no frame for this long (protocol/SPEC.md, "Join rules").</summary>
    public static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(5);

    // How long a closing connection may take to send its last frames and see the phone close too. It
    // bounds I/O with a phone that has stopped reading, so it runs on real time, not the TimeProvider,
    // and a test that freezes the clock can still close.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    // Frames waiting to go out. A phone reads its replies at once, so a full outbox means it has stopped
    // reading while it keeps sending, and the connection is aborted rather than left to grow.
    private const int OutboxLimit = 64;

    // How long the accept loop waits after an error that may last, such as running out of sockets.
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly byte[] PingFrame = ControlFrame.Encode(new Ping());
    private static readonly byte[] PongFrame = ControlFrame.Encode(new Pong());
    private static readonly byte[] ByeFrame = ControlFrame.Encode(new Bye());

    private readonly IJoinHandler _handler;
    private readonly PairingCode _code;
    private readonly IPairingStore _store;
    private readonly TimeProvider _time;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;

    // Guards the sets below and every call to the handler.
    private readonly Lock _gate = new();
    private readonly HashSet<Connection> _connections = [];
    private readonly Dictionary<string, Connection> _joined = []; // by deviceId
    private Task? _disposing;
    private long _framesRead;

    /// <summary>
    /// Listens on an IPv4 endpoint and starts accepting phones. Port 0 picks a free port. v1 runs over
    /// IPv4 only (docs/decisions.md), so an IPv6 endpoint throws <see cref="ArgumentException"/>.
    /// </summary>
    public ControlServer(IJoinHandler handler, PairingCode code, IPairingStore store, IPEndPoint localEndPoint, TimeProvider? time = null)
    {
        if (localEndPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The control server listens on IPv4 only.", nameof(localEndPoint));
        _handler = handler;
        _code = code;
        _store = store;
        _time = time ?? TimeProvider.System;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // As for the input socket: no other process can take the port's connections.
            if (OperatingSystem.IsWindows())
                _listener.ExclusiveAddressUse = true;
            _listener.Bind(localEndPoint);
            _listener.Listen();
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
        LocalEndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        _accepting = AcceptAsync();
    }

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// A connection ended on an unexpected exception, such as the handler or the store throwing. Only
    /// that connection closes. Raised on the connection's thread, outside the server's lock, once the
    /// connection is torn down; an exception a subscriber throws is ignored.
    /// </summary>
    public event Action<Exception>? ConnectionFailed;

    /// <summary>Connections accepted and not yet torn down, for tests.</summary>
    internal int OpenConnections
    {
        get
        {
            lock (_gate)
                return _connections.Count;
        }
    }

    /// <summary>Whole frames read from phones so far, for tests.</summary>
    internal long FramesRead => Interlocked.Read(ref _framesRead);

    /// <summary>
    /// Stops accepting, sends <c>bye</c> to every connection and closes it, and returns once every welcomed
    /// phone has had its <see cref="IJoinHandler.Leave"/>.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
            _disposing ??= StopAsync();
        return new ValueTask(_disposing);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task StopAsync()
    {
        _stopping.Cancel();
        _listener.Dispose();
        await _accepting.ConfigureAwait(false);
        Connection[] connections;
        lock (_gate)
            connections = [.. _connections];
        foreach (var connection in connections)
            connection.Close(ByeFrame);
        await Task.WhenAll(connections.Select(connection => connection.Completion)).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                if (_stopping.IsCancellationRequested)
                    return;
                // A phone that reset before it was accepted is gone at once; other errors may last.
                if (e.SocketErrorCode != SocketError.ConnectionReset && !await RetryDelayAsync().ConfigureAwait(false))
                    return;
                continue;
            }
            Connection connection;
            try
            {
                connection = new Connection(this, socket);
            }
            catch (SocketException)
            {
                socket.Dispose(); // reset before it could be set up
                continue;
            }
            lock (_gate)
                _connections.Add(connection);
            connection.Start();
        }
    }

    // False once the server is stopping.
    private async Task<bool> RetryDelayAsync()
    {
        try
        {
            await Task.Delay(AcceptRetryDelay, _stopping.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // On the connection's read loop, from its JoinSession.
    private Welcome? Join(Connection connection, ConnectedPhone phone, ReadOnlySpan<byte> sessionKey)
    {
        lock (_gate)
        {
            // Its reply would never be sent, and the handler would plug a pad only to remove it.
            if (_stopping.IsCancellationRequested || connection.IsClosing)
                return null;
            if (_handler.Join(phone, sessionKey) is not { } welcome)
                return null;
            _joined.TryGetValue(phone.DeviceId, out var replaced);
            _joined[phone.DeviceId] = connection;
            // The old connection leaves once it has closed, after this Join.
            replaced?.Close(ByeFrame);
            return welcome;
        }
    }

    // Returns what Leave threw, for the caller to report outside the lock.
    private Exception? TornDown(Connection connection, ConnectedPhone? phone)
    {
        lock (_gate)
        {
            _connections.Remove(connection);
            if (phone is null)
                return null;
            if (_joined.TryGetValue(phone.DeviceId, out var holder) && holder == connection)
                _joined.Remove(phone.DeviceId);
            try
            {
                _handler.Leave(phone);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }

    private void Report(Exception? failure)
    {
        if (failure is null)
            return;
        try
        {
            ConnectionFailed?.Invoke(failure);
        }
        catch (Exception)
        {
            // A subscriber that throws must not stop the teardown.
        }
    }

    /// <summary>
    /// One phone's connection. The read loop decodes frames and feeds the join session; replies, pings and
    /// the last frames go through the outbox, which one writer sends in order. Closing sends what is
    /// queued, shuts the sending side, and reads on until the phone closes too: closing a socket with
    /// unread bytes resets it, and a reset can lose the last frames before the phone reads them.
    /// </summary>
    private sealed class Connection
    {
        private readonly ControlServer _server;
        private readonly Socket _socket;
        private readonly JoinSession _session;
        private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboxLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _abort = new(); // ends both loops at once
        private readonly ITimer _ping;
        private readonly ITimer _quiet;
        private int _closing;

        public Connection(ControlServer server, Socket socket)
        {
            socket.NoDelay = true; // first: it throws for a phone that has reset already
            _server = server;
            _socket = socket;
            var phone = ((IPEndPoint)socket.RemoteEndPoint!).Address;
            _session = new JoinSession(phone, server._code, server._store, (p, key) => server.Join(this, p, key));
            _ping = server._time.CreateTimer(_ => Send(PingFrame), null, PingInterval, PingInterval);
            _quiet = server._time.CreateTimer(_ => Abort(), null, QuietTimeout, Timeout.InfiniteTimeSpan);
        }

        public Task Completion { get; private set; } = Task.CompletedTask;

        public bool IsClosing => Volatile.Read(ref _closing) != 0;

        public void Start() => Completion = RunAsync();

        /// <summary>
        /// Sends the last frame, if any, after what is queued, then closes within <see cref="CloseTimeout"/>.
        /// Only the first call counts.
        /// </summary>
        public void Close(byte[]? lastFrame)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;
            _ping.Dispose();
            if (lastFrame is not null)
                _outbox.Writer.TryWrite(lastFrame);
            _outbox.Writer.TryComplete();
            try
            {
                _abort.CancelAfter(CloseTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        private void Send(byte[] frame)
        {
            // After Close the outbox takes nothing more, which is fine.
            if (!_outbox.Writer.TryWrite(frame) && !IsClosing)
                Abort();
        }

        // Closes at once, dropping anything not yet sent.
        private void Abort()
        {
            Close(null);
            try
            {
                _abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        private async Task RunAsync()
        {
            await Task.Yield(); // off the accept loop
            var writing = WriteAsync();
            Exception? failure = null;
            try
            {
                await ReadAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The link dropped, or this side closed it.
            }
            catch (Exception e)
            {
                failure = e;
                Abort();
            }
            finally
            {
                Close(null);
                await writing.ConfigureAwait(false);
                _socket.Dispose();
                _quiet.Dispose();
                _ping.Dispose();
                _abort.Dispose();
                _session.Dispose();
                var leaveFailure = _server.TornDown(this, _session.Phone);
                _server.Report(failure);
                _server.Report(leaveFailure);
            }
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[ControlFrame.HeaderSize + ControlFrame.MaxBodySize];
            while (true)
            {
                if (!await ReadExactlyAsync(buffer.AsMemory(0, ControlFrame.HeaderSize)).ConfigureAwait(false))
                    return;
                if (IsClosing)
                    break;
                // The length alone says whether to read on: 0 or over 4096 closes at once, without the body.
                if (ControlFrame.TryRead(buffer.AsSpan(0, ControlFrame.HeaderSize), out _) is not FrameStatus.Incomplete)
                {
                    Abort();
                    return;
                }
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                if (!await ReadExactlyAsync(buffer.AsMemory(ControlFrame.HeaderSize, length)).ConfigureAwait(false))
                    return;
                if (IsClosing)
                    break;
                Interlocked.Increment(ref _server._framesRead);
                _quiet.Change(QuietTimeout, Timeout.InfiniteTimeSpan);
                var step = _session.Receive(buffer.AsSpan(ControlFrame.HeaderSize, length));
                if (step.Close)
                {
                    Close(step.Reply is null ? null : ControlFrame.Encode(step.Reply));
                    break;
                }
                if (step.Reply is not null)
                    Send(step.Reply is Pong ? PongFrame : ControlFrame.Encode(step.Reply));
            }
            // Closing: read and drop the rest until the phone closes too, or CloseTimeout passes.
            while (await _socket.ReceiveAsync(buffer, SocketFlags.None, _abort.Token).ConfigureAwait(false) > 0)
            {
            }
        }

        // False if the phone closed the connection first.
        private async Task<bool> ReadExactlyAsync(Memory<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var count = await _socket.ReceiveAsync(buffer, SocketFlags.None, _abort.Token).ConfigureAwait(false);
                if (count == 0)
                    return false;
                buffer = buffer[count..];
            }
            return true;
        }

        private async Task WriteAsync()
        {
            try
            {
                await foreach (var frame in _outbox.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
                    await _socket.SendAsync(frame, SocketFlags.None, _abort.Token).ConfigureAwait(false);
                // Every frame is out, so the phone sees the close after them.
                _socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The link dropped, or the connection was aborted.
            }
        }
    }
}
