using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// The same scripted slaves as the virtual-bus tests, delivered synchronously at the test
/// transport boundary. Each reply is acknowledged by the master's reader before time advances.
/// Confirmation and arrival stamps use the scheduler's clock, as they do in production.
/// </summary>
internal sealed class ClockedLssRig : IDisposable
{
    private readonly List<ScriptedLssSlave> _slaves = new();
    private readonly List<Task> _deliveries = new();
    private readonly List<IDisposable> _responses = new();
    private readonly ProtocolActor _slaveTimers;
    public VirtualClock Clock { get; } = new();
    public ProtocolActor Timers { get; }
    public LssTestService Service { get; } = new();
    public LssMaster Master { get; }

    public ClockedLssRig(LssMasterOptions? options = null, bool parkReader = false)
    {
        Clock.Advance(TimeSpan.FromSeconds(1)); // Zero is reserved for an absent transport stamp.
        Timers = Clock.NewActor();
        _slaveTimers = Clock.NewActor();
        Service.Send = SendAsync;
        Service.Subscription.ParkReader = parkReader;
        Master = new LssMaster(Service, options ?? new LssMasterOptions(), ownsService: false, Timers);
    }

    public ScriptedLssSlave Add(LssAddress address, byte nodeId = CanOpenCobId.UnconfiguredNodeId)
    {
        var service = new LssTestService();
        var slave = new ScriptedLssSlave(service, address, nodeId, delay: DelayAsync);
        _slaves.Add(slave);
        return slave;
    }

    public async Task<T> RunAsync<T>(Task<T> operation)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!operation.IsCompleted)
        {
            if (DateTime.UtcNow >= limit) throw new TimeoutException("The clocked LSS operation did not settle.");
            await FlushRepliesAsync();
            var next = await Timers.NextTimerDelayAsync();
            if (next is { } delay)
            {
                var response = await _slaveTimers.NextTimerDelayAsync();
                if (response is { } earlier && earlier < delay) delay = earlier;
                await Clock.AdvanceAsync(delay);
                await FlushRepliesAsync();
            }
            else
                await Task.Yield();
        }
        return await operation;
    }

    public Task ArmedAsync(TimeSpan delay)
        => Clock.WaitUntilTimerArmedAsync(Timers, delay, TimeSpan.FromSeconds(5));

    public Task InjectAsync(byte[] payload, int id = (int)CanOpenCobId.LssSlaveMessage, bool extended = false)
        => Service.Subscription.Enqueue(new CanFrameEvent(
            new CanFrameView(CanFrameType.Can20, id, payload, extended ? FrameFlags.Ext : FrameFlags.None),
            false, TimeSpan.Zero, Clock.Elapsed.Ticks));

    public void Dispose()
    {
        Master.Dispose();
        foreach (var slave in _slaves) slave.Dispose();
        foreach (var response in _responses) response.Dispose();
        Service.Dispose();
        Clock.Dispose();
    }

    private async Task<TxConfirmation> SendAsync(CanFrame frame)
    {
        long sent = Clock.Elapsed.Ticks;
        var immediate = new List<Task>();
        foreach (var slave in _slaves)
        {
            var reply = slave.Process(frame.Data.ToArray());
            if (reply is null) continue;
            if (slave.ResponseDelay > TimeSpan.Zero)
            {
                _responses.Add(_slaveTimers.Schedule(slave.ResponseDelay, () =>
                {
                    var delivery = InjectAsync(reply);
                    lock (_deliveries) _deliveries.Add(delivery);
                }));
            }
            else
                immediate.Add(InjectAsync(reply));
        }
        await Task.WhenAll(immediate);
        return new TxConfirmation { Confirmed = true, HostHandoffTimestamp = sent, HostTransmitTimestamp = sent };
    }

    private async Task FlushRepliesAsync()
    {
        Task[] pending;
        lock (_deliveries)
        {
            pending = _deliveries.ToArray();
            _deliveries.Clear();
        }
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = _slaveTimers.Schedule(delay, () => elapsed.TrySetResult(true));
        using var cancelled = cancellationToken.Register(() => elapsed.TrySetCanceled());
        await elapsed.Task;
    }
}
