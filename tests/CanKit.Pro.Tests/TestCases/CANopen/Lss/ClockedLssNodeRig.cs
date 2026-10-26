using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// A real node and master with transport acknowledgements on a controlled clock. Time only
/// advances after each node's reader, actor and reserved sends have processed the probe.
/// </summary>
internal sealed class ClockedLssNodeRig : IDisposable
{
    private readonly VirtualClock _clock = new();
    private readonly ProtocolActor _timers;
    private readonly LssTestService _service = new();
    private readonly List<(CanOpenNode Node, LssTestService Service)> _nodes = new();

    public LssMaster Master { get; }
    public ProtocolActor Timers => _timers;
    public ConcurrentQueue<uint> NodeTransmitIds { get; } = new();

    public ClockedLssNodeRig(LssMasterOptions? options = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        _timers = _clock.NewActor();
        _service.Send = SendAsync;
        Master = new LssMaster(_service, options ?? new LssMasterOptions(), ownsService: false, _timers);
    }

    public CanOpenNode Add(byte nodeId = CanOpenCobId.UnconfiguredNodeId, LssSlaveOptions? lss = null)
    {
        var service = new LssTestService { Send = ReplyAsync };
        var node = new CanOpenNode(service, nodeId,
            new CanOpenNodeOptions { Lss = lss ?? new LssSlaveOptions() }, ownsService: false);
        _nodes.Add((node, service));
        return node;
    }

    // A scan is many actor and reader hand-offs, so on a starved host the same virtual work takes
    // far more wall time (0.3 s idle took tens of seconds with the test host pinned to one core and
    // 15 busy loops, #315). The guard therefore asks for progress, not for a total: it fails when
    // the virtual clock has not moved forward for the stall budget of wall time, and when one
    // operation has run longer than MaxVirtualRun of virtual time,
    // which is deterministic and catches a loop that keeps re-arming timers. Only an advance by
    // more than zero counts as progress: a loop that re-arms a zero-delay timer would otherwise
    // refresh the stall clock on every pass and the detector itself would never end.
    //
    // The default stall budget is SchedulingBudget for every node plus one: while the master sends,
    // SendAsync waits for each node's reply to drain, up to SchedulingBudget per node, and no
    // master timer is armed meanwhile, so the clock cannot move. A send that takes longer than one
    // budget in total while every node stays within its own is slow, not stuck.
    public async Task<T> RunAsync<T>(Task<T> operation, TimeSpan? stallBudget = null)
    {
        // TimeSpan * int does not exist on net48, hence the ticks.
        var stall = stallBudget ?? TimeSpan.FromTicks(LssTestTiming.SchedulingBudget.Ticks * (_nodes.Count + 1));
        var guard = new ClockedOperationGuard(MonotonicTimeSource.Instance, _clock.Elapsed, stall);
        while (!operation.IsCompleted)
        {
            guard.Check(_clock.Elapsed);
            if (await _timers.NextTimerDelayAsync() is { } delay)
            {
                await _clock.AdvanceAsync(delay);
                guard.NoteAdvance(delay);
            }
            else
                await Task.Delay(1);
        }
        return await operation;
    }

    public async Task TransmitAsync(uint cobId, byte[] payload)
    {
        using var frame = CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false);
        await SendAsync(frame);
    }

    public void Dispose()
    {
        Master.Dispose();
        foreach (var (node, service) in _nodes)
        {
            node.Dispose();
            service.Dispose();
        }
        _service.Dispose();
        _clock.Dispose();
    }

    private async Task<TxConfirmation> SendAsync(CanFrame frame)
    {
        long sent = _clock.Elapsed.Ticks;
        foreach (var (node, service) in _nodes)
        {
            await service.Subscription.Enqueue(Frame(frame));
            // Enqueue completes after the reader posts the frame; this getter is an actor barrier.
            _ = ((ILssSlave)node).State;
            var limit = DateTime.UtcNow + LssTestTiming.SchedulingBudget;
            while (node.HasLssTransmitsInFlight)
            {
                if (DateTime.UtcNow >= limit) throw new TimeoutException("The node reply did not drain.");
                await Task.Delay(1);
            }
        }
        return new TxConfirmation { Confirmed = true, HostHandoffTimestamp = sent, HostTransmitTimestamp = sent };
    }

    private async Task<TxConfirmation> ReplyAsync(CanFrame frame)
    {
        long sent = _clock.Elapsed.Ticks;
        NodeTransmitIds.Enqueue((uint)frame.ID);
        await _service.Subscription.Enqueue(Frame(frame));
        return new TxConfirmation { Confirmed = true, HostHandoffTimestamp = sent, HostTransmitTimestamp = sent };
    }

    private CanFrameEvent Frame(CanFrame frame)
        => new(new CanFrameView(CanFrameType.Can20, frame.ID, frame.Data.ToArray(), FrameFlags.None),
            false, TimeSpan.Zero, _clock.Elapsed.Ticks);
}
