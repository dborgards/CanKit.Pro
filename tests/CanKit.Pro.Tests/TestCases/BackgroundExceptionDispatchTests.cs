using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.Addressing;
using CanKit.Pro.CANopen;
using CanKit.Pro.IsoTp;
using CanKit.Pro.J1939;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;
using J1939TpFactory = CanKit.Pro.J1939Tp.J1939Tp;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// <c>BackgroundExceptionOccurred</c> in every protocol layer -- J1939-TP, ISO-TP, the J1939 node,
/// the CANopen node -- is raised off the layer's actor loop, after whatever the report announces
/// is in place, and to each subscriber on its own (#356 T-2, #381).
/// </summary>
/// <remarks>
/// Every wait below is a signal; <see cref="HangGuard"/> bounds a hang and measures nothing. The
/// one negative window (<see cref="Reports_Do_Not_Overlap"/>) can only pass falsely on a slow
/// host, never fail falsely.
/// </remarks>
public class BackgroundExceptionDispatchTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static string NewSession() => $"bgex-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(HangGuard)).ConfigureAwait(false) != task)
            throw new TimeoutException($"Nothing arrived within {HangGuard}: the actor is stuck, or the report was lost.");
        return await task.ConfigureAwait(false);
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // =========================================================================================
    // The dispatcher itself
    // =========================================================================================

    [Fact]
    public async Task A_Report_Is_Delivered_Off_The_Thread_That_Made_It()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var deliveredOn = NewSignal<int>();
        EventHandler<Exception> handler = (_, _) => deliveredOn.TrySetResult(Environment.CurrentManagedThreadId);

        dispatcher.Raise(this, handler, new InvalidOperationException("report"));

        (await Within(deliveredOn.Task)).Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Keep_The_Report_From_The_Next()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var second = NewSignal<Exception>();
        EventHandler<Exception> handler = (_, _) => throw new InvalidOperationException("first subscriber");
        handler += (_, ex) => second.TrySetResult(ex);
        var report = new InvalidOperationException("report");

        dispatcher.Raise(this, handler, report);

        (await Within(second.Task)).Should().BeSameAs(report);
    }

    [Fact]
    public void Nothing_Is_Queued_Without_A_Subscriber()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        dispatcher.Invoking(d => d.Raise(this, null, new InvalidOperationException("nobody listens")))
            .Should().NotThrow();
    }

    [Fact]
    public async Task A_Delivery_That_Throws_Does_Not_Strand_The_Ones_Behind_It()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var after = NewSignal<bool>();

        dispatcher.Post(() => throw new InvalidOperationException("a layer's own delivery failed"));
        dispatcher.Post(() => after.TrySetResult(true));

        (await Within(after.Task)).Should().BeTrue();
    }

    // Reports made while one is still being delivered wait for it and keep their order.
    [Fact]
    public async Task Reports_Are_Delivered_In_The_Order_They_Were_Made()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        using var release = new ManualResetEventSlim();
        var holding = NewSignal<bool>();
        var seen = new List<int>();
        var last = NewSignal<bool>();
        const int Count = 50;

        dispatcher.Post(() =>
        {
            holding.TrySetResult(true);
            release.Wait(HangGuard);
        });
        await Within(holding.Task);
        for (int i = 0; i < Count; i++)
        {
            int n = i;
            dispatcher.Post(() =>
            {
                lock (seen) seen.Add(n);
                if (n == Count - 1) last.TrySetResult(true);
            });
        }
        release.Set();

        await Within(last.Task);
        lock (seen) seen.Should().Equal(Enumerable.Range(0, Count));
    }

    // A report made from inside a subscriber is delivered after that subscriber has returned, not
    // beside it: the reports made on an actor loop never overlapped, and a subscriber written for
    // that may rely on it. A negative window: it can only pass falsely on a slow host.
    [Fact]
    public async Task Reports_Do_Not_Overlap()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        using var secondRan = new ManualResetEventSlim();
        var overlapped = NewSignal<bool>();
        EventHandler<Exception> second = (_, _) => secondRan.Set();

        dispatcher.Post(() =>
        {
            dispatcher.Raise(this, second, new InvalidOperationException("made during the first"));
            overlapped.TrySetResult(secondRan.Wait(TimeSpan.FromMilliseconds(300)));
        });

        (await Within(overlapped.Task)).Should().BeFalse();
        secondRan.Wait(HangGuard).Should().BeTrue("it is delivered once the first has returned");
    }
}
