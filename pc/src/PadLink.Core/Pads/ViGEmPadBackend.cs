using System.Collections.Concurrent;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;

namespace PadLink.Core.Pads;

/// <summary>
/// Virtual pads from the ViGEmBus driver, through Nefarius.ViGEm.Client. The constructor throws the
/// library's exception if the driver is missing or can't be opened.
/// </summary>
public sealed class ViGEmPadBackend : IVirtualPadBackend
{
    private readonly ViGEmClient _client = new();

    // Submit looks pads up without a lock; Create, Remove and Dispose take _gate.
    private readonly ConcurrentDictionary<PadId, Pad> _pads = new();

    // Removed pads are unplugged at once but freed only in Dispose, so a Submit already past its
    // lookup reaches an unplugged pad, which ignores it, instead of freed memory.
    private readonly List<Pad> _removed = [];
    private readonly Lock _gate = new();
    private int _lastId;
    private volatile bool _disposed;

    public event Action<PadId, PadFeedback>? Feedback;

    public PadId Create(PadType type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = new PadId(++_lastId);
            Pad pad = type switch
            {
                PadType.X360 => new XboxPad(this, id, _client.CreateXbox360Controller()),
                PadType.Ds4 => new Ds4Pad(this, id, _client.CreateDualShock4Controller()),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };
            // Held before it's plugged in: the driver's first notification can arrive during Connect,
            // and a Feedback handler may look the pad up.
            _pads[id] = pad;
            try
            {
                pad.Connect();
            }
            catch
            {
                _pads.TryRemove(id, out _);
                throw;
            }
            return id;
        }
    }

    public void Submit(PadId pad, in PadState state) => Find(pad).Submit(state);

    public void Remove(PadId pad)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pads.TryRemove(pad, out var removed))
                throw NotHeld(pad);
            _removed.Add(removed);
            removed.Unplug();
        }
    }

    public int? ReadUserIndex(PadId pad) => Find(pad).UserIndex;

    /// <summary>The library's own <c>UserIndex</c> for an Xbox 360 pad, for the driver tests.</summary>
    internal int? LibraryUserIndex(PadId pad) => Find(pad) is XboxPad xbox ? xbox.LibraryUserIndex : null;

    /// <summary>
    /// Submit with the pad already looked up, as a Submit racing Remove has it, for the driver tests.
    /// </summary>
    internal Action<PadState> LookedUpSubmit(PadId pad)
    {
        var found = Find(pad);
        return state => found.Submit(state);
    }

    /// <summary>
    /// Unplugs the pads left, in the order they were created, and frees every pad. Throws an
    /// <see cref="AggregateException"/> once everything is freed if any pad failed to unplug.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            List<Exception> failures = [];
            foreach (var (_, pad) in _pads.OrderBy(held => held.Key.Value))
            {
                try
                {
                    pad.Unplug();
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
                _removed.Add(pad);
            }
            _pads.Clear();
            foreach (var pad in _removed)
                pad.Free();
            _removed.Clear();
            _client.Dispose();
            if (failures.Count > 0)
                throw new AggregateException("Some pads failed to unplug", failures);
        }
    }

    private Pad Find(PadId pad)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pads.TryGetValue(pad, out var found) ? found : throw NotHeld(pad);
    }

    private static ArgumentException NotHeld(PadId pad) => new($"This backend holds no pad {pad.Value}", nameof(pad));

    private void OnFeedback(PadId pad, PadFeedback feedback) => Feedback?.Invoke(pad, feedback);

    private abstract class Pad(IVirtualGamepad controller)
    {
        public virtual int? UserIndex => null;

        public abstract void Submit(in PadState state);

        public void Connect()
        {
            controller.AutoSubmitReport = false;
            try
            {
                controller.Connect();
            }
            catch
            {
                // The library plugs the pad in before it registers for notifications, so a failure
                // there leaves it plugged in.
                try
                {
                    controller.Disconnect();
                }
                catch
                {
                    // Keep Connect's exception.
                }
                Free();
                throw;
            }
        }

        /// <summary>Removes the pad from the driver. Disconnect unregisters the feedback callback first.</summary>
        public void Unplug() => controller.Disconnect();

        /// <summary>Frees the library's pad, once nothing can submit to it.</summary>
        public void Free() => (controller as IDisposable)?.Dispose();
    }

    private sealed class XboxPad : Pad
    {
        private const int UserIndices = 4; // XInput's

        private readonly IXbox360Controller _controller;

        // The LED number from the driver's latest notification, or -1 before the first. Windows gives
        // a pad's player LED the same number as its XInput user index, and 255 to a fifth pad.
        private volatile int _ledNumber = -1;

        public XboxPad(ViGEmPadBackend backend, PadId id, IXbox360Controller controller)
            : base(controller)
        {
            _controller = controller;
            controller.FeedbackReceived += (_, e) =>
            {
                _ledNumber = e.LedNumber;
                backend.OnFeedback(id, new PadFeedback { LargeMotor = e.LargeMotor, SmallMotor = e.SmallMotor, LedNumber = e.LedNumber });
            };
        }

        public override int? UserIndex
        {
            get
            {
                var led = _ledNumber;
                return led is >= 0 and < UserIndices ? led : null;
            }
        }

        public int? LibraryUserIndex
        {
            get
            {
                try
                {
                    return _controller.UserIndex;
                }
                catch (Xbox360UserIndexNotReportedException)
                {
                    return null;
                }
            }
        }

        public override void Submit(in PadState state)
        {
            var report = PadMapping.ToX360(state);
            _controller.SetButtonsFull((ushort)report.Buttons);
            _controller.LeftTrigger = report.LeftTrigger;
            _controller.RightTrigger = report.RightTrigger;
            _controller.LeftThumbX = report.ThumbLX;
            _controller.LeftThumbY = report.ThumbLY;
            _controller.RightThumbX = report.ThumbRX;
            _controller.RightThumbY = report.ThumbRY;
            _controller.SubmitReport();
        }
    }

    private sealed class Ds4Pad : Pad
    {
        private readonly IDualShock4Controller _controller;

        public Ds4Pad(ViGEmPadBackend backend, PadId id, IDualShock4Controller controller)
            : base(controller)
        {
            _controller = controller;
            // The library deprecates this event for AwaitRawOutputReport(), a blocking read that would
            // need a thread per pad. Rumble and the lightbar come in PLAN.md 3.4, which picks one.
#pragma warning disable CS0618
            controller.FeedbackReceived += (_, e) => backend.OnFeedback(id, new PadFeedback
            {
                LargeMotor = e.LargeMotor,
                SmallMotor = e.SmallMotor,
                Lightbar = new Rgb(e.LightbarColor.Red, e.LightbarColor.Green, e.LightbarColor.Blue),
            });
#pragma warning restore CS0618
        }

        public override void Submit(in PadState state)
        {
            var report = PadMapping.ToDs4(state);
            _controller.SetButtonsFull(report.ButtonWord);
            _controller.SetSpecialButtonsFull((byte)report.Special);
            _controller.LeftTrigger = report.TriggerL;
            _controller.RightTrigger = report.TriggerR;
            _controller.LeftThumbX = report.ThumbLX;
            _controller.LeftThumbY = report.ThumbLY;
            _controller.RightThumbX = report.ThumbRX;
            _controller.RightThumbY = report.ThumbRY;
            _controller.SubmitReport();
        }
    }
}
