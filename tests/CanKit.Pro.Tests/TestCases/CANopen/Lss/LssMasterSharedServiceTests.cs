using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// L-2 (#359): the LSS master and a <see cref="CanOpenNode"/> LSS slave on one host service whose
/// adapter flags every frame this host sent as an echo — SocketCAN loopback, or Kvaser and PCAN in
/// echo mode. The slave's reply reaches the master only as such an echo.
/// </summary>
/// <remarks>
/// The response window runs on a manual clock that only the test moves, and only once the reply is
/// on the bus (or, for the master alone, once its own request's echo has come back). Nothing here
/// decides on the wall clock; it bounds a hand-off between threads.
/// </remarks>
public class LssMasterSharedServiceTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_Master_And_A_Node_Sharing_One_Echo_Flagging_Service_Complete_A_Confirmed_Service()
    {
        using var rig = new Rig("lss-shared-echo");
        using var replies = rig.Service.Subscribe(
            f => (uint)f.Frame.ID == CanOpenCobId.LssSlaveMessage, includeEcho: true);
        // Unconfigured, so the node-id the master assigns becomes active at once.
        using var node = new CanOpenNode(rig.Service, CanOpenCobId.UnconfiguredNodeId,
            new CanOpenNodeOptions { Lss = new LssSlaveOptions() }, ownsService: false, rig.Time);

        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration).WithTimeoutAsync(LssTestTiming.SchedulingBudget);
        await WaitUntilAsync(() => node.LssState() == LssState.Configuration);

        var configure = rig.Master.ConfigureNodeIdAsync(0x21);
        // The slave's reply is on the bus, flagged as this host's echo. Only then may the window end.
        using (var budget = new CancellationTokenSource(LssTestTiming.SchedulingBudget))
            (await replies.WaitToReadAsync(budget.Token)).Should().BeTrue();
        await rig.EndResponseWindowAsync();

        var result = await configure.WithTimeoutAsync(LssTestTiming.SchedulingBudget);
        result.Succeeded.Should().BeTrue("the reply of a node on the same service arrives as an echo");
        await WaitUntilAsync(() => node.NodeId == 0x21);
    }

    [Fact]
    public async Task The_Master_Does_Not_Take_The_Echo_Of_Its_Own_Request_For_A_Reply()
    {
        // Configure node-id: request and reply share the command specifier 11h, so an echo of the
        // request would parse as a reply with error code 21h.
        using var rig = new Rig("lss-own-echo");
        using var requests = rig.Service.Subscribe(
            f => (uint)f.Frame.ID == CanOpenCobId.LssMasterMessage, includeEcho: true);

        var configure = rig.Master.ConfigureNodeIdAsync(0x21);
        using (var budget = new CancellationTokenSource(LssTestTiming.SchedulingBudget))
            (await requests.WaitToReadAsync(budget.Token)).Should().BeTrue();
        requests.TryRead(out var echo).Should().BeTrue();
        echo.IsEcho.Should().BeTrue("the adapter flags the master's own request as this host's echo");
        await rig.EndResponseWindowAsync();

        var silent = async () => await configure.WithTimeoutAsync(LssTestTiming.SchedulingBudget);
        await silent.Should().ThrowAsync<LssTimeoutException>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + LssTestTiming.SchedulingBudget;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The LSS slave did not reach the expected state.");
            await Task.Delay(1);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ControllableBus _bus;
        private readonly ProtocolActor _timers;

        public Rig(string session)
        {
            Time.Advance(TimeSpan.FromSeconds(1)); // Zero is reserved for an absent transport stamp.
            _timers = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, Time, null);
            _bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession(session));
            Service = new CanBusService(_bus, Time.GetTimestamp);
            Master = new LssMaster(Service, new LssMasterOptions { ResponseTimeout = ResponseTimeout },
                ownsService: false, _timers);
        }

        public ManualTimeSource Time { get; } = new();

        public CanBusService Service { get; }

        public LssMaster Master { get; }

        /// <summary>Moves the clock past the response window and lets the master's timer fire.</summary>
        public async Task EndResponseWindowAsync()
        {
            Time.Advance(ResponseTimeout + TimeSpan.FromMilliseconds(1));
            // Two round-trips: the first wakes the actor, the second proves the due timer has run.
            await _timers.PostAsync(() => 0);
            await _timers.PostAsync(() => 0);
        }

        public void Dispose()
        {
            Master.Dispose();
            Service.Dispose();
            _bus.Dispose();
            _timers.Dispose();
        }
    }
}
