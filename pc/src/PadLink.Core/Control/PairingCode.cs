using System.Globalization;
using System.Security.Cryptography;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Control;

/// <summary>
/// The 6-digit code a new phone pairs with (docs/design.md, "Security and privacy"; protocol/SPEC.md,
/// "Join sequence"). It works only while shown, expires after 5 minutes, and is used up by a successful
/// pairing. After 5 wrong tries no code works for 30 seconds, then a new one appears, which caps online
/// guessing at 10 tries a minute. The code itself never leaves the PC: phones send a proof, which
/// <see cref="TryUse"/> checks.
/// </summary>
public sealed class PairingCode : IDisposable
{
    public const int MaxWrongTries = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<int> _nextNumber;
    private readonly ITimer _timer; // the code's expiry, or the end of a cooldown
    private bool _shown;
    private string? _code; // null while hidden or cooling down
    private string? _lastCode;
    private long _since; // when the code was made, or the cooldown began
    private int _wrongTries;

    public PairingCode(TimeProvider? time = null)
        : this(time, () => RandomNumberGenerator.GetInt32(1_000_000))
    {
    }

    /// <summary>For tests: nextNumber gives each code, 0–999999.</summary>
    internal PairingCode(TimeProvider? time, Func<int> nextNumber)
    {
        _time = time ?? TimeProvider.System;
        _nextNumber = nextNumber;
        _timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Raised with the new code, or null once hidden or cooling down, each time the code changes. It is
    /// raised under the code's lock, so changes arrive in order; a handler must not wait for another
    /// thread that uses this code.
    /// </summary>
    public event Action<string?>? Changed;

    /// <summary>The code to show, or null while hidden or cooling down.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
                return _code;
        }
    }

    /// <summary>Whether pairing is on but waiting out the cooldown after 5 wrong tries.</summary>
    public bool IsCoolingDown
    {
        get
        {
            lock (_gate)
                return _shown && _code is null;
        }
    }

    /// <summary>
    /// Turns pairing on with a fresh code. Does nothing while a code is shown, or during a cooldown, so
    /// showing again can't cut the cooldown short.
    /// </summary>
    public void Show()
    {
        lock (_gate)
        {
            if (_shown)
                return;
            _shown = true;
            Replace();
        }
    }

    /// <summary>Turns pairing off: no proof matches until <see cref="Show"/> makes a fresh code.</summary>
    public void Hide()
    {
        lock (_gate)
        {
            if (!_shown)
                return;
            _shown = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_code is null)
                return; // cooling down: already null to the outside
            _code = null;
            Changed?.Invoke(null);
        }
    }

    /// <summary>
    /// Checks a <c>pair</c> message's proof against the shown code. The right proof uses the code up, and
    /// a new one replaces it. A wrong one counts as a try, and the 5th starts the cooldown. While hidden
    /// or cooling down, every proof is wrong and none counts.
    /// </summary>
    public bool TryUse(ReadOnlySpan<byte> pcPublicKey, ReadOnlySpan<byte> phonePublicKey, ReadOnlySpan<byte> proof)
    {
        lock (_gate)
        {
            if (_code is null)
                return false;
            // The expiry timer may not have run yet.
            if (_time.GetElapsedTime(_since) >= Lifetime)
            {
                Replace();
                return false;
            }
            if (JoinKeys.CheckProof(_code, pcPublicKey, phonePublicKey, proof))
            {
                Replace();
                return true;
            }
            if (++_wrongTries >= MaxWrongTries)
            {
                _code = null;
                _since = _time.GetTimestamp();
                _timer.Change(Cooldown, Timeout.InfiniteTimeSpan);
                Changed?.Invoke(null);
            }
            return false;
        }
    }

    public void Dispose() => _timer.Dispose();

    private void OnTimer()
    {
        lock (_gate)
        {
            if (!_shown)
                return;
            // A timer may fire a little early, or after a newer code reset it.
            var left = (_code is null ? Cooldown : Lifetime) - _time.GetElapsedTime(_since);
            if (left > TimeSpan.Zero)
                _timer.Change(left, Timeout.InfiniteTimeSpan);
            else
                Replace();
        }
    }

    // Under the lock. A new code always differs from the last one.
    private void Replace()
    {
        string code;
        do
            code = _nextNumber().ToString("D6", CultureInfo.InvariantCulture);
        while (code == _lastCode);
        _code = _lastCode = code;
        _wrongTries = 0;
        _since = _time.GetTimestamp();
        _timer.Change(Lifetime, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(code);
    }
}
