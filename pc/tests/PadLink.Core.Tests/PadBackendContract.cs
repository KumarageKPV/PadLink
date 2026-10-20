using System.Diagnostics;
using PadLink.Core.Pads;
using PadLink.Protocol;

namespace PadLink.Core.Tests;

/// <summary>
/// What every <see cref="IVirtualPadBackend"/> does, run against the fake and against the driver, so
/// tests that use <see cref="FakePadBackend"/> see the real backend's behaviour.
/// </summary>
public abstract class PadBackendContract(IVirtualPadBackend backend) : IDisposable
{
    // The driver reports an Xbox pad's index within about 20 ms of creating it (spike/RESULTS.md).
    private static readonly TimeSpan UserIndexTimeout = TimeSpan.FromSeconds(2);

    protected IVirtualPadBackend Backend { get; } = backend;

    public void Dispose()
    {
        Backend.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Each_pad_gets_its_own_id()
    {
        PadId[] pads = [Backend.Create(PadType.X360), Backend.Create(PadType.Ds4), Backend.Create(PadType.X360), Backend.Create(PadType.Ds4)];

        Assert.Equal(pads.Length, pads.Distinct().Count());
    }

    [Fact]
    public void Both_pad_types_take_any_state()
    {
        var full = new PadState { Buttons = (PadButtons)0xffff, Lx = short.MinValue, Ly = short.MaxValue, Rx = -1, Ry = 1, Lt = 255, Rt = 255 };

        foreach (var type in new[] { PadType.X360, PadType.Ds4 })
        {
            var pad = Backend.Create(type);
            Backend.Submit(pad, full);
            Backend.Submit(pad, default);
        }
    }

    [Fact]
    public void A_removed_pad_takes_no_more_calls()
    {
        var pad = Backend.Create(PadType.X360);
        Backend.Remove(pad);

        Assert.Throws<ArgumentException>(() => Backend.Submit(pad, default));
        Assert.Throws<ArgumentException>(() => Backend.ReadUserIndex(pad));
        Assert.Throws<ArgumentException>(() => Backend.Remove(pad));
    }

    [Fact]
    public void A_pad_the_backend_never_created_takes_no_calls()
    {
        var pad = new PadId(int.MaxValue);

        Assert.Throws<ArgumentException>(() => Backend.Submit(pad, default));
        Assert.Throws<ArgumentException>(() => Backend.ReadUserIndex(pad));
        Assert.Throws<ArgumentException>(() => Backend.Remove(pad));
    }

    [Fact]
    public void A_disposed_backend_takes_no_more_calls()
    {
        var pad = Backend.Create(PadType.Ds4);
        Backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Backend.Create(PadType.X360));
        Assert.Throws<ObjectDisposedException>(() => Backend.Submit(pad, default));
        Assert.Throws<ObjectDisposedException>(() => Backend.ReadUserIndex(pad));
        Assert.Throws<ObjectDisposedException>(() => Backend.Remove(pad));
    }

    [Fact]
    public void Xbox_pads_get_user_indices_0_to_3_in_the_order_they_were_created()
    {
        var first = Backend.Create(PadType.X360);
        var ds4 = Backend.Create(PadType.Ds4);
        PadId[] xbox = [first, Backend.Create(PadType.X360), Backend.Create(PadType.X360), Backend.Create(PadType.X360)];

        Assert.Equal([0, 1, 2, 3], xbox.Select(WaitForUserIndex));
        Assert.Null(Backend.ReadUserIndex(ds4));
    }

    [Fact]
    public void A_new_xbox_pad_takes_the_lowest_free_user_index()
    {
        PadId[] xbox = [Backend.Create(PadType.X360), Backend.Create(PadType.X360), Backend.Create(PadType.X360)];
        Assert.Equal([0, 1, 2], xbox.Select(WaitForUserIndex));

        Backend.Remove(xbox[1]);
        var next = Backend.Create(PadType.X360);

        Assert.Equal(1, WaitForUserIndex(next));
    }

    [Fact]
    public void A_fifth_xbox_pad_gets_no_user_index()
    {
        // XInput has 4 user indices (CLAUDE.md, Gotchas).
        PadId[] xbox = [Backend.Create(PadType.X360), Backend.Create(PadType.X360), Backend.Create(PadType.X360), Backend.Create(PadType.X360)];
        Assert.Equal([0, 1, 2, 3], xbox.Select(WaitForUserIndex));

        Assert.Null(WaitForUserIndex(Backend.Create(PadType.X360)));
    }

    protected int? WaitForUserIndex(PadId pad) => WaitForUserIndex(Backend, pad);

    /// <summary>The pad's user index once the backend knows it, or null if it doesn't within the timeout.</summary>
    internal static int? WaitForUserIndex(IVirtualPadBackend backend, PadId pad)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var index = backend.ReadUserIndex(pad);
            if (index is not null || clock.Elapsed > UserIndexTimeout)
                return index;
            Thread.Sleep(5);
        }
    }
}
