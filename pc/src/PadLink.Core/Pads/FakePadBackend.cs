namespace PadLink.Core.Pads;

/// <summary>
/// A backend without the driver, for tests and for running the host without one. It records every
/// call that succeeds, in order, and keeps them all for as long as it runs. Xbox 360 pads get the
/// lowest free user index of 0–3, as the driver gives them, but at once rather than about 20 ms later.
/// </summary>
public sealed class FakePadBackend : IVirtualPadBackend
{
    private const int UserIndices = 4;

    private readonly Lock _gate = new();
    private readonly List<PadCall> _calls = [];
    private readonly List<(PadId Id, PadType Type, int? UserIndex)> _pads = []; // in the order they were created
    private int _lastId;
    private bool _disposed;

    public event Action<PadId, PadFeedback>? Feedback;

    /// <summary>A copy of the calls so far.</summary>
    public IReadOnlyList<PadCall> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public PadId Create(PadType type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pad = new PadId(++_lastId);
            _pads.Add((pad, type, type == PadType.X360 ? LowestFreeUserIndex() : null));
            _calls.Add(new PadCall.Created(pad, type));
            return pad;
        }
    }

    public void Submit(PadId pad, in PadState state)
    {
        lock (_gate)
        {
            Find(pad);
            _calls.Add(new PadCall.Submitted(pad, state));
        }
    }

    public void Remove(PadId pad)
    {
        lock (_gate)
        {
            _pads.RemoveAt(Find(pad));
            _calls.Add(new PadCall.Removed(pad));
        }
    }

    public int? ReadUserIndex(PadId pad)
    {
        lock (_gate)
        {
            var userIndex = _pads[Find(pad)].UserIndex;
            _calls.Add(new PadCall.UserIndexRead(pad));
            return userIndex;
        }
    }

    /// <summary>
    /// Raises <see cref="Feedback"/> for the pad, on the caller's thread. A <see cref="PadFeedback.LedNumber"/>
    /// for an Xbox 360 pad becomes its user index, as the driver's notification does: 0–3, or none
    /// for anything higher.
    /// </summary>
    public void RaiseFeedback(PadId pad, PadFeedback feedback)
    {
        lock (_gate)
        {
            var i = Find(pad);
            if (_pads[i].Type == PadType.X360 && feedback.LedNumber is { } led)
                _pads[i] = _pads[i] with { UserIndex = led < UserIndices ? led : null };
        }
        Feedback?.Invoke(pad, feedback);
    }

    /// <summary>Removes the pads left, recording each, in the order they were created.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var (pad, _, _) in _pads)
                _calls.Add(new PadCall.Removed(pad));
            _pads.Clear();
        }
    }

    private int Find(PadId pad)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var i = _pads.FindIndex(held => held.Id == pad);
        return i >= 0 ? i : throw new ArgumentException($"This backend holds no pad {pad.Value}", nameof(pad));
    }

    private int? LowestFreeUserIndex()
    {
        for (var userIndex = 0; userIndex < UserIndices; userIndex++)
        {
            if (!_pads.Exists(held => held.UserIndex == userIndex))
                return userIndex;
        }
        return null;
    }
}

/// <summary>A call that <see cref="FakePadBackend"/> recorded.</summary>
public abstract record PadCall
{
    private PadCall()
    {
    }

    public sealed record Created(PadId Pad, PadType Type) : PadCall;

    public sealed record Submitted(PadId Pad, PadState State) : PadCall;

    public sealed record Removed(PadId Pad) : PadCall;

    public sealed record UserIndexRead(PadId Pad) : PadCall;
}
