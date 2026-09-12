using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// NFR-008 concurrency stress: the RawCan demultiplex service, shared by many protocol instances
/// at once, must be race-free under parallel subscription churn while traffic keeps flowing.
/// Repeated enough times to give races a chance, bounded enough for CI (~a few seconds).
///
/// The companion registry stress test stayed in CanKit.Pro.legacy: it exercises CanKit's own
/// CanRegistry through internals the published package does not expose, so it is a test of
/// CanKit, not of anything here. See docs/upstream-candidates.md.
/// </summary>
public class RawCanConcurrencyTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(60);

    private static string NewSession() => $"rawcan-stress-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    // NFR-008 (demux): N parallel Subscribe/Dispose cycles against continuous RX traffic must
    // not throw, must not starve a long-lived subscription, and must only ever deliver frames
    // matching each subscription's own filter.
    [Fact]
    public async Task CanBusService_Parallel_Subscribe_Dispose_Under_Traffic_No_Races()
    {
        var session = NewSession();
        using var trafficBus = Open(session, 0);
        using var serviceBus = Open(session, 1);
        using var service = new CanBusService(serviceBus);

        using var cts = new CancellationTokenSource(Bounded);

        // Long-lived control subscription: proves the demux keeps dispatching for the whole run.
        var controlCount = 0;
        using var control = service.Subscribe(_ => true);
        var controlReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in control.Frames.WithCancellation(cts.Token))
                {
                    Interlocked.Increment(ref controlCount);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        });

        // Continuous two-ID traffic against the service's bus.
        var pump = Task.Run(() =>
        {
            var flip = false;
            while (!cts.IsCancellationRequested)
            {
                trafficBus.Transmit(CanFrame.Classic(flip ? 0x100 : 0x200, new byte[] { 1 }));
                flip = !flip;
            }
        });

        var exceptions = new ConcurrentQueue<Exception>();
        var workers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            try
            {
                var myId = w % 2 == 0 ? 0x100 : 0x200;
                for (var i = 0; i < 25; i++)
                {
                    using var sub = service.Subscribe(f => f.Frame.ID == myId);
                    // Drain briefly, then dispose mid-stream (the churn under test).
                    using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                    try
                    {
                        await foreach (var frameEvent in sub.Frames.WithCancellation(readCts.Token))
                        {
                            frameEvent.Frame.ID.Should().Be(myId,
                                "a subscription must only ever observe its own filter's frames");
                        }
                    }
                    catch (OperationCanceledException) { /* expected: our short drain window */ }
                }
            }
            catch (Exception ex) { exceptions.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(workers);
        cts.Cancel();
        try { await pump; } catch { /* transmit-in-flight races shutdown */ }
        try { await controlReader; } catch { /* idem */ }

        exceptions.Should().BeEmpty(
            "parallel Subscribe/Dispose churn under traffic must be race-free (NFR-008)");
        controlCount.Should().BeGreaterThan(0,
            "the long-lived subscription must keep receiving frames throughout the churn (no starvation)");
    }
}
