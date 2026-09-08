using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// FR-RAW-051 verification: a simulated BusOff state must abort an active L3 (ISO-TP) send
/// with a defined, observable error instead of letting it hang until a protocol timeout.
/// </summary>
public class IsoTpBusOffTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"isotp-busoff-{Guid.NewGuid():N}";

    // An echo-capable bus that accepts frames but never echoes them: the First Frame's
    // SendConfirmed stays pending, which is the deterministic window in which the BusOff
    // transition has to resolve the confirmation. Driving that from the double rather than an
    // adapter keeps the test about ISO-TP's reaction, not about how one adapter reaches BusOff.
    private static ControllableBus OpenSilentEcho()
    {
        var bus = ControllableBus.EchoCapable(NewSession());
        bus.EchoAcceptedFrames = false;
        return bus;
    }

    private static IsoTpChannelOptions FastOptions()
        => new()
        {
            UseCanFd = false,
            UsePadding = true,
            LocalBlockSize = 0,
            LocalStMin = TimeSpan.Zero,
            // Long protocol timers: if the send faults quickly, it must be the BusOff path,
            // not N_As/N_Bs expiring.
            NAs = TimeSpan.FromSeconds(10),
            NBs = TimeSpan.FromSeconds(10),
            NCr = TimeSpan.FromSeconds(10),
            WftMax = 10,
        };

    [Fact]
    public async Task Active_MultiFrame_Send_Faults_With_BusOff_Instead_Of_Hanging()
    {
        using var bus = OpenSilentEcho();

        using var sender = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x300, 0x301), FastOptions());

        // Multi-frame send: the FF goes out and its TX confirmation stays pending behind the
        // blocked echo. Driving the bus off while that confirmation is outstanding must abort
        // the send (L2 -> L3 propagation per FR-RAW-051), not hang.
        var send = sender.SendAsync(Enumerable.Range(0, 30).Select(i => (byte)i).ToArray());

        // Give the channel a moment to register the pending FF confirmation.
        await Task.Delay(100);
        bus.BusState = BusState.BusOff;
        bus.RaiseFault(new InvalidOperationException("simulated bus-off"));

        var sw = Stopwatch.StartNew();
        Func<Task> act = async () =>
        {
            var completed = await Task.WhenAny(send, Task.Delay(ShortTimeout));
            if (completed != send) throw new TimeoutException("SendAsync hung past the test bound.");
            await send;
        };
        var ex = (await act.Should().ThrowAsync<IsoTpException>()).Which;
        sw.Stop();

        ex.Message.Should().Contain("BusOff",
            "the failed TX confirmation (BusOff) must surface as an observable ISO-TP error");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the abort must be immediate, not via the (10 s) N_As/N_Bs protocol timers");
    }
}
