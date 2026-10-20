using System.Collections.Concurrent;
using System.Diagnostics;
using PadLink.Core.Pads;
using PadLink.Protocol;

namespace PadLink.Core.Tests;

[Collection(DriverCollection.Name)]
[Trait("Category", "RequiresViGEm")]
public class ViGEmPadBackendContractTests() : PadBackendContract(new ViGEmPadBackend());

[Collection(DriverCollection.Name)]
[Trait("Category", "RequiresViGEm")]
public sealed class ViGEmPadBackendTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan XInputTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(2);

    private readonly ViGEmPadBackend _backend = new();

    public void Dispose() => _backend.Dispose();

    [Fact]
    public void Padprobe_sees_4_xbox_and_4_ds4_pads_then_8_ds4_pads_then_none()
    {
        // Mixed mode makes the Xbox pads first, in slot order (docs/design.md, "Virtual controller layer").
        var pads = new List<PadId>();
        for (var i = 0; i < 4; i++)
            pads.Add(_backend.Create(PadType.X360));
        for (var i = 0; i < 4; i++)
            pads.Add(_backend.Create(PadType.Ds4));
        Expect("x360=4,ds4=4");

        foreach (var pad in pads)
            _backend.Remove(pad);
        for (var i = 0; i < 8; i++)
            _backend.Create(PadType.Ds4);
        Expect("x360=0,ds4=8");

        _backend.Dispose();
        Expect("x360=0,ds4=0");
    }

    // docs/gates/M0.md, "Carried forward": the spike read 0 from the driver's user-index call for
    // every Xbox pad, while the LED numbers in its notifications gave 0–3.
    [Fact]
    public void An_xbox_pads_user_index_is_the_led_number_the_driver_reports()
    {
        var leds = new ConcurrentDictionary<PadId, byte>();
        _backend.Feedback += (pad, feedback) =>
        {
            if (feedback.LedNumber is { } led)
                leds[pad] = led;
        };
        PadId[] pads = [_backend.Create(PadType.X360), _backend.Create(PadType.X360), _backend.Create(PadType.X360), _backend.Create(PadType.X360)];
        // The backend stores the LED number just before it raises the event that fills leds.
        WaitUntil(() => leds.Count == pads.Length);

        var rows = pads.Select(pad => (
            Read: PadBackendContract.WaitForUserIndex(_backend, pad),
            Library: _backend.LibraryUserIndex(pad),
            Led: leds.TryGetValue(pad, out var led) ? led : (int?)null)).ToArray();

        output.WriteLine("slot  ReadUserIndex  library UserIndex  LedNumber in feedback");
        for (var i = 0; i < rows.Length; i++)
            output.WriteLine($"{i + 1,4}  {rows[i].Read,13}  {rows[i].Library,17}  {rows[i].Led,21}");
        Assert.Equal([(0, 0, 0), (1, 1, 1), (2, 2, 2), (3, 3, 3)], rows.Select(r => (r.Read, r.Library, r.Led)));
    }

    [Fact]
    public void A_fifth_xbox_pads_led_number_is_255()
    {
        var leds = new ConcurrentDictionary<PadId, byte>();
        _backend.Feedback += (pad, feedback) =>
        {
            if (feedback.LedNumber is { } led)
                leds[pad] = led;
        };
        for (var i = 0; i < 4; i++)
            _backend.Create(PadType.X360);
        var fifth = _backend.Create(PadType.X360);

        WaitUntil(() => leds.ContainsKey(fifth));

        output.WriteLine($"fifth pad: LedNumber {leds[fifth]}, ReadUserIndex {_backend.ReadUserIndex(fifth)?.ToString() ?? "null"}");
        Assert.Equal(255, leds[fifth]);
        Assert.Null(_backend.ReadUserIndex(fifth));
    }

    // docs/gates/M0.md, "Carried forward": nothing sent input to an Xbox pad before this.
    [Fact]
    public void Each_xbox_pad_shows_its_state_through_xinput_at_its_user_index()
    {
        PadId[] pads = [_backend.Create(PadType.X360), _backend.Create(PadType.X360), _backend.Create(PadType.X360), _backend.Create(PadType.X360)];
        var indices = pads.Select(pad => PadBackendContract.WaitForUserIndex(_backend, pad) ?? throw new TimeoutException($"{pad} got no user index")).ToArray();
        // Every button but Guide, which would open the Xbox Game Bar. Sticks and triggers differ per
        // pad, so each user index must show its own pad.
        var states = pads.Select((_, k) => new PadState
        {
            Buttons = (PadButtons)0x7fff & ~PadButtons.Guide,
            Lx = (short)(1000 * (k + 1)),
            Ly = short.MinValue,
            Rx = short.MaxValue,
            Ry = (short)(-1000 * (k + 1)),
            Lt = (byte)(10 + k),
            Rt = (byte)(250 - k),
        }).ToArray();

        for (var k = 0; k < pads.Length; k++)
            _backend.Submit(pads[k], states[k]);
        for (var k = 0; k < pads.Length; k++)
            AssertXInputShows(indices[k], PadMapping.ToX360(states[k]));

        foreach (var pad in pads)
            _backend.Submit(pad, default);
        foreach (var index in indices)
            AssertXInputShows(index, default);
    }

    // Submitting is the end of the hot path, which must not allocate (CLAUDE.md, Rules).
    [Fact]
    public void Submitting_to_both_pad_types_allocates_nothing()
    {
        var xbox = _backend.Create(PadType.X360);
        var ds4 = _backend.Create(PadType.Ds4);
        PadState[] states = [default, new() { Buttons = PadButtons.FaceBottom | PadButtons.DpadUp, Lx = 1000, Ly = -1000, Lt = 50 }];
        for (var i = 0; i < 10; i++)
            SubmitBoth(i);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            SubmitBoth(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        void SubmitBoth(int i)
        {
            _backend.Submit(xbox, states[i & 1]);
            _backend.Submit(ds4, states[i & 1]);
        }
    }

    // A Submit that races Remove, already past its lookup, must reach an unplugged pad, which ignores
    // it, and never a freed one.
    [Fact]
    public void A_submit_past_its_lookup_when_the_pad_is_removed_does_nothing()
    {
        var xbox = _backend.Create(PadType.X360);
        var ds4 = _backend.Create(PadType.Ds4);
        var userIndex = PadBackendContract.WaitForUserIndex(_backend, xbox) ?? throw new TimeoutException($"{xbox} got no user index");
        var submitXbox = _backend.LookedUpSubmit(xbox);
        var submitDs4 = _backend.LookedUpSubmit(ds4);
        _backend.Remove(xbox);
        _backend.Remove(ds4);

        var pressed = new PadState { Buttons = PadButtons.FaceBottom, Lx = 1000 };
        submitXbox(pressed);
        submitDs4(pressed);

        WaitUntil(() => XInput.Read(userIndex) is null);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > NotificationTimeout)
                throw new TimeoutException($"Still waiting after {NotificationTimeout.TotalSeconds:0} s");
            Thread.Sleep(5);
        }
    }

    private void Expect(string counts)
    {
        var (exitCode, messages) = PadProbe.Expect(counts);
        output.WriteLine($"padprobe --expect \"{counts}\": exit {exitCode}, {messages}");
        Assert.Equal(0, exitCode);
    }

    private void AssertXInputShows(int userIndex, X360Report expected)
    {
        var clock = Stopwatch.StartNew();
        var shown = XInput.Read(userIndex);
        while (shown != expected && clock.Elapsed < XInputTimeout)
        {
            Thread.Sleep(5);
            shown = XInput.Read(userIndex);
        }
        output.WriteLine($"XInput {userIndex}: {shown}");
        Assert.Equal(expected, shown);
    }
}
