namespace PadLink.Core.Pads;

/// <summary>
/// A backend without the driver, for tests and for running the host without one. It records every
/// call that succeeds, in order, and keeps them all for as long as it runs; with
/// <see cref="RecordSubmits"/> off, Submit calls are left out. Xbox 360 pads get the lowest free user
/// index of 0–3, as the driver gives them, but at once rather than about 20 ms later.
/// </summary>
public sealed class FakePadBackend : IVirtualPadBackend
{
    private const int UserIndices = 4;

    private readonly Lock _gate = new();
    private readonly List<PadCall> _calls = [];
    private readonly List<HeldPad> _pads = []; // in the order they were created
    private int _lastId;
    private bool _disposed;

    public event Action<PadId, PadFeedback>? Feedback;

    /// <summary>
    /// Whether Submit calls go into <see cref="Calls"/>. Off, a pad keeps only its last state and a
    /// count, so a long run doesn't grow and Submit allocates nothing.
    /// </summary>
    public bool RecordSubmits { get; init; } = true;

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
            _pads.Add(new HeldPad(pad, type, type == PadType.X360 ? LowestFreeUserIndex() : null));
            _calls.Add(new PadCall.Created(pad, type));
            return pad;
        }
    }

    public void Submit(PadId pad, in PadState state)
    {
        lock (_gate)
        {
            var i = Find(pad);
            _pads[i] = _pads[i] with { State = state, Submits = _pads[i].Submits + 1 };
            if (RecordSubmits)
                _calls.Add(new PadCall.Submitted(pad, state));
        }
    }

    /// <summary>The state the pad shows: the last one submitted, or neutral before any.</summary>
    public PadState StateOf(PadId pad)
    {
        lock (_gate)
            return _pads[Find(pad)].State;
    }

    /// <summary>How many times the pad was submitted to, recorded or not.</summary>
    public long SubmitCount(PadId pad)
    {
        lock (_gate)
            return _pads[Find(pad)].Submits;
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
            foreach (var held in _pads)
                _calls.Add(new PadCall.Removed(held.Id));
            _pads.Clear();
        }
    }

    // A loop rather than FindIndex, whose lambda would capture pad and allocate on every Submit.
    private int Find(PadId pad)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var i = 0; i < _pads.Count; i++)
        {
            if (_pads[i].Id == pad)
                return i;
        }
        throw new ArgumentException($"This backend holds no pad {pad.Value}", nameof(pad));
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

    private readonly record struct HeldPad(PadId Id, PadType Type, int? UserIndex, PadState State = default, long Submits = 0);
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
