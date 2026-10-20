using PadLink.Core.Pads;
using PadLink.Protocol;

namespace PadLink.Core.Tests;

public class FakePadBackendTests() : PadBackendContract(new FakePadBackend())
{
    private FakePadBackend Fake => (FakePadBackend)Backend;

    [Fact]
    public void Every_call_is_recorded_in_order()
    {
        var state = new PadState { Buttons = PadButtons.FaceBottom, Lx = 100, Rt = 7 };

        var xbox = Fake.Create(PadType.X360);
        var ds4 = Fake.Create(PadType.Ds4);
        Fake.Submit(ds4, state);
        Fake.ReadUserIndex(xbox);
        Fake.Remove(xbox);
        Fake.Submit(ds4, default);

        Assert.Equal<PadCall>(
        [
            new PadCall.Created(xbox, PadType.X360),
            new PadCall.Created(ds4, PadType.Ds4),
            new PadCall.Submitted(ds4, state),
            new PadCall.UserIndexRead(xbox),
            new PadCall.Removed(xbox),
            new PadCall.Submitted(ds4, default),
        ], Fake.Calls);
    }

    [Fact]
    public void Calls_that_throw_are_not_recorded()
    {
        var pad = Fake.Create(PadType.Ds4);
        Fake.Remove(pad);

        Assert.Throws<ArgumentException>(() => Fake.Submit(pad, default));

        Assert.Equal<PadCall>([new PadCall.Created(pad, PadType.Ds4), new PadCall.Removed(pad)], Fake.Calls);
    }

    [Fact]
    public void Dispose_removes_the_pads_left_in_the_order_they_were_created()
    {
        var first = Fake.Create(PadType.Ds4);
        var gone = Fake.Create(PadType.X360);
        var last = Fake.Create(PadType.X360);
        Fake.Remove(gone);

        Fake.Dispose();

        Assert.Equal<PadCall>([new PadCall.Removed(first), new PadCall.Removed(last)], Fake.Calls.Skip(4));
    }

    [Fact]
    public void Feedback_a_test_raises_reaches_subscribers_with_its_pad()
    {
        var pad = Fake.Create(PadType.Ds4);
        var feedback = new PadFeedback { LargeMotor = 200, SmallMotor = 3, Lightbar = new Rgb(1, 2, 3) };
        var received = new List<(PadId, PadFeedback)>();
        Fake.Feedback += (id, f) => received.Add((id, f));

        Fake.RaiseFeedback(pad, feedback);

        Assert.Equal([(pad, feedback)], received);
    }

    [Fact]
    public void An_led_number_in_feedback_becomes_the_xbox_pads_user_index()
    {
        var xbox = Fake.Create(PadType.X360);
        var ds4 = Fake.Create(PadType.Ds4);

        Fake.RaiseFeedback(xbox, new PadFeedback { LedNumber = 2 });
        Fake.RaiseFeedback(ds4, new PadFeedback { LedNumber = 1 });
        Assert.Equal((2, (int?)null), (Fake.ReadUserIndex(xbox), Fake.ReadUserIndex(ds4)));

        Fake.RaiseFeedback(xbox, new PadFeedback { LedNumber = 255 });
        Assert.Null(Fake.ReadUserIndex(xbox));

        Fake.RaiseFeedback(xbox, new PadFeedback { LargeMotor = 9 });
        Assert.Null(Fake.ReadUserIndex(xbox));
    }

    [Fact]
    public void A_pad_shows_its_last_state_and_counts_its_submits()
    {
        var state = new PadState { Buttons = PadButtons.Start, Ry = -9 };
        var pad = Fake.Create(PadType.X360);
        Assert.Equal((default(PadState), 0), (Fake.StateOf(pad), Fake.SubmitCount(pad)));

        Fake.Submit(pad, default);
        Fake.Submit(pad, state);

        Assert.Equal((state, 2), (Fake.StateOf(pad), Fake.SubmitCount(pad)));
    }

    [Fact]
    public void Without_recording_submits_only_the_last_state_and_count_are_kept()
    {
        using var fake = new FakePadBackend { RecordSubmits = false };
        var state = new PadState { Buttons = PadButtons.FaceTop, Lt = 4 };
        var pad = fake.Create(PadType.Ds4);

        fake.Submit(pad, default);
        fake.Submit(pad, state);
        fake.Remove(pad);

        Assert.Equal<PadCall>([new PadCall.Created(pad, PadType.Ds4), new PadCall.Removed(pad)], fake.Calls);
        Assert.Throws<ArgumentException>(() => fake.StateOf(pad));
        Assert.Throws<ArgumentException>(() => fake.SubmitCount(pad));
    }

    // The receiver's allocation test submits to this mode (PLAN.md 2.6), and a long host run keeps it.
    [Fact]
    public void Submit_without_recording_allocates_nothing()
    {
        using var fake = new FakePadBackend { RecordSubmits = false };
        fake.Create(PadType.X360);
        var pad = fake.Create(PadType.Ds4);
        var state = new PadState { Lx = 1 };
        fake.Submit(pad, state); // warm-up

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            fake.Submit(pad, state);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(10_001, fake.SubmitCount(pad));
    }
}
