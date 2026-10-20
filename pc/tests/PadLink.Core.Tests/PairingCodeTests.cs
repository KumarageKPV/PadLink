using Microsoft.Extensions.Time.Testing;
using PadLink.Core.Control;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Tests;

public sealed class PairingCodeTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly PairingCode _code;
    private readonly List<string?> _changes = [];
    private readonly X25519KeyPair _pc = X25519KeyPair.Generate();
    private readonly X25519KeyPair _phone = X25519KeyPair.Generate();

    public PairingCodeTests()
    {
        _code = new PairingCode(_time);
        _code.Changed += _changes.Add;
    }

    public void Dispose()
    {
        _code.Dispose();
        _pc.Dispose();
        _phone.Dispose();
    }

    [Fact]
    public void Hidden_until_shown_and_then_6_ascii_digits()
    {
        Assert.Null(_code.Current);

        _code.Show();

        Assert.Matches("^[0-9]{6}$", _code.Current);
        Assert.Equal([_code.Current], _changes);
    }

    [Fact]
    public void Showing_again_keeps_the_code()
    {
        _code.Show();
        var shown = _code.Current;

        _code.Show();

        Assert.Equal(shown, _code.Current);
        Assert.Single(_changes);
    }

    [Fact]
    public void The_right_proof_is_accepted_once_and_uses_the_code_up()
    {
        _code.Show();
        var shown = _code.Current!;

        Assert.True(TryUse(shown));

        Assert.NotNull(_code.Current);
        Assert.NotEqual(shown, _code.Current);
        Assert.Equal([shown, _code.Current], _changes);
        Assert.False(TryUse(shown));
    }

    [Fact]
    public void Four_wrong_tries_keep_the_code_and_the_fifth_starts_a_30_second_cooldown()
    {
        _code.Show();
        var shown = _code.Current!;
        var wrong = Other(shown);

        for (var i = 0; i < PairingCode.MaxWrongTries - 1; i++)
            Assert.False(TryUse(wrong));
        Assert.Equal(shown, _code.Current);
        Assert.False(_code.IsCoolingDown);

        Assert.False(TryUse(wrong));

        Assert.Null(_code.Current);
        Assert.True(_code.IsCoolingDown);
        Assert.Equal([shown, null], _changes);
        Assert.False(TryUse(shown));
    }

    [Fact]
    public void During_the_cooldown_every_proof_fails_and_none_counts_and_then_a_new_code_appears()
    {
        _code.Show();
        var shown = _code.Current!;
        for (var i = 0; i < PairingCode.MaxWrongTries; i++)
            TryUse(Other(shown));

        _time.Advance(PairingCode.Cooldown - TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 3 * PairingCode.MaxWrongTries; i++)
            Assert.False(TryUse(shown));
        Assert.Null(_code.Current);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(_code.IsCoolingDown);
        Assert.NotNull(_code.Current);
        Assert.NotEqual(shown, _code.Current);
        Assert.Equal([shown, null, _code.Current], _changes);
        Assert.True(TryUse(_code.Current!));
    }

    [Fact]
    public void Hiding_during_the_cooldown_keeps_the_code_hidden_and_showing_waits_for_the_cooldown()
    {
        _code.Show();
        for (var i = 0; i < PairingCode.MaxWrongTries; i++)
            TryUse(Other(_code.Current!));

        _code.Hide();
        _time.Advance(PairingCode.Cooldown);

        Assert.Null(_code.Current);
        Assert.False(_code.IsCoolingDown);
        Assert.Equal(2, _changes.Count);

        _code.Show();
        for (var i = 0; i < PairingCode.MaxWrongTries; i++)
            TryUse(Other(_code.Current!));
        _code.Show();

        Assert.Null(_code.Current);
        Assert.True(_code.IsCoolingDown);
    }

    [Fact]
    public void A_new_code_starts_with_no_wrong_tries()
    {
        _code.Show();
        for (var i = 0; i < PairingCode.MaxWrongTries; i++)
            TryUse(Other(_code.Current!));
        _time.Advance(PairingCode.Cooldown);
        var second = _code.Current!;

        for (var i = 0; i < PairingCode.MaxWrongTries - 1; i++)
            Assert.False(TryUse(Other(second)));

        Assert.True(TryUse(second));
    }

    [Fact]
    public void The_code_works_until_5_minutes_and_is_replaced_at_5_minutes()
    {
        _code.Show();
        var shown = _code.Current!;

        _time.Advance(PairingCode.Lifetime - TimeSpan.FromMilliseconds(1));
        Assert.Equal(shown, _code.Current);
        Assert.Single(_changes);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.NotNull(_code.Current);
        Assert.NotEqual(shown, _code.Current);
        Assert.Equal(2, _changes.Count);
        Assert.False(TryUse(shown));
    }

    [Fact]
    public void A_replaced_code_gets_its_own_5_minutes()
    {
        _code.Show();
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(TryUse(_code.Current!));
        var second = _code.Current!;

        _time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(second, _code.Current);
        Assert.True(TryUse(second));
    }

    [Fact]
    public void While_hidden_every_proof_fails_and_no_try_counts()
    {
        _code.Show();
        var shown = _code.Current!;

        _code.Hide();

        Assert.Null(_code.Current);
        Assert.Equal([shown, null], _changes);
        for (var i = 0; i < 2 * PairingCode.MaxWrongTries; i++)
            Assert.False(TryUse(shown));
        _time.Advance(PairingCode.Lifetime * 2);
        Assert.Null(_code.Current);
        Assert.Equal(2, _changes.Count);
    }

    [Fact]
    public void Showing_after_hiding_makes_a_fresh_code()
    {
        _code.Show();
        _time.Advance(TimeSpan.FromMinutes(4));
        _code.Hide();

        _code.Show();
        var shown = _code.Current!;
        _time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(shown, _code.Current);
        Assert.True(TryUse(shown));
    }

    [Fact]
    public void A_new_code_never_repeats_the_last_one_even_after_a_cooldown()
    {
        var numbers = new Queue<int>([42, 42, 917, 917, 917, 5]);
        using var code = new PairingCode(_time, numbers.Dequeue);

        code.Show();
        Assert.Equal("000042", code.Current);
        Assert.True(TryUse(code, "000042"));
        Assert.Equal("000917", code.Current);
        for (var i = 0; i < PairingCode.MaxWrongTries; i++)
            TryUse(code, "000000");
        _time.Advance(PairingCode.Cooldown);

        Assert.Equal("000005", code.Current);
    }

    [Fact]
    public void An_expired_code_fails_even_if_its_timer_has_not_run()
    {
        var time = new TimeWithIdleTimers();
        using var code = new PairingCode(time);
        code.Show();
        var shown = code.Current!;

        time.Advance(PairingCode.Lifetime);

        Assert.Equal(shown, code.Current); // the timer never ran
        Assert.False(TryUse(code, shown));
        Assert.NotNull(code.Current);
        Assert.NotEqual(shown, code.Current);
    }

    private bool TryUse(string code) => TryUse(_code, code);

    private bool TryUse(PairingCode pairingCode, string code) =>
        pairingCode.TryUse(_pc.PublicKey, _phone.PublicKey, JoinKeys.Proof(code, _pc.PublicKey, _phone.PublicKey));

    private static string Other(string code) => code == "000000" ? "000001" : "000000";
}

/// <summary>A fake clock whose timers never fire, as if they were late.</summary>
internal sealed class TimeWithIdleTimers : FakeTimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();

    private sealed class IdleTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
