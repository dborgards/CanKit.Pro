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

    public async Task<T> RunAsync<T>(Task<T> operation)
    {
        var limit = DateTime.UtcNow + LssTestTiming.SchedulingBudget;
        while (!operation.IsCompleted)
        {
            if (DateTime.UtcNow >= limit) throw new TimeoutException("The clocked node scan did not settle.");
            if (await _timers.NextTimerDelayAsync() is { } delay)
                await _clock.AdvanceAsync(delay);
            else
                await Task.Yield();
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
                await Task.Yield();
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
