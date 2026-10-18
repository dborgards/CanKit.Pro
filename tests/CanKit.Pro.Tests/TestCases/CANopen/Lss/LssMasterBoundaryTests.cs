using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterBoundaryTests
{
    [Theory]
    [InlineData(CanFrameType.Can20, FrameFlags.None, 0x7F)]
    [InlineData(CanFrameType.CanFd, FrameFlags.None, 0x42)]
    [InlineData(CanFrameType.Can20, FrameFlags.Error, 0x42)]
    [InlineData(CanFrameType.Can20, FrameFlags.Rtr, 0x42)]
    public async Task The_Response_ID_Accepts_Only_Classical_Data_Frames(
        CanFrameType kind, FrameFlags flags, byte expectedNodeId)
    {
        using var rig = new ClockedLssRig();
        rig.Service.Send = async frame =>
        {
            var candidate = LssFrames.Command(LssCommand.InquireNodeId);
            candidate[1] = 0x7F;
            await rig.Service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(kind, (int)CanOpenCobId.LssSlaveMessage, candidate, flags),
                false, TimeSpan.Zero, rig.Clock.Elapsed.Ticks)).WaitAsync(TimeSpan.FromSeconds(5));
            var valid = LssFrames.Command(LssCommand.InquireNodeId);
            valid[1] = 0x42;
            await rig.InjectAsync(valid).WaitAsync(TimeSpan.FromSeconds(5));
            // Both frames have been handled by the subscription before confirmation returns.
            return new TxConfirmation { Confirmed = true };
        };

        (await rig.Master.InquireNodeIdAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(expectedNodeId);
    }

    [Theory]
    [InlineData(499, false, false)] // Before handoff.
    [InlineData(500, false, true)] // At handoff.
    [InlineData(1000, false, true)] // At transmit completion.
    [InlineData(0, false, true)] // Missing timestamps do not mean stale frames.
    [InlineData(-1, false, true)]
    [InlineData(0, true, true)] // At the deadline.
    [InlineData(1, true, false)] // Immediately after the deadline.
    public async Task Inquire_Filters_Queued_Replies_By_Their_Arrival_Timestamp(
        long timestamp, bool relativeToDeadline, bool accepted)
    {
        using var service = new LssTestService();
        var timeout = TimeSpan.FromSeconds(1);
        long deadline = 1000 + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        long arrival = relativeToDeadline ? deadline + timestamp : timestamp;
        service.Send = async frame =>
        {
            var reply = new byte[LssFrames.Length];
            reply[0] = frame.Data.Span[0];
            reply[1] = 0x42;
            await service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, reply, FrameFlags.None),
                isEcho: false, receiveTimestamp: TimeSpan.Zero, hostArrivalTimestamp: arrival)).WaitAsync(TimeSpan.FromSeconds(5));
            // Resume the sender only after the master has buffered the reply. The absolute
            // deadline is already past, so neither acceptance nor rejection needs a timer.
            return new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = 500,
                HostTransmitTimestamp = 1000,
            };
        };
        using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions { ResponseTimeout = timeout });

        if (accepted)
            (await master.InquireNodeIdAsync()).Should().Be(0x42);
        else
        {
            var inquire = async () => await master.InquireNodeIdAsync();
            await inquire.Should().ThrowAsync<LssTimeoutException>();
        }
    }

    [Fact]
    public async Task Dispose_Leaves_A_Parked_Readers_Cancellation_Source_Alive()
    {
        using var service = new LssTestService();
        service.Subscription.ParkReader = true;
        using var master = CanOpenLss.OpenMaster(service, leaveOpen: false);
        try
        {
            await service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            master.Dispose();

            service.Disposals.Should().Be(1);
            service.Subscription.Disposed.Should().BeTrue();
            service.Subscription.Finished.Task.IsCompleted.Should().BeFalse();
            service.Subscription.Release.TrySetResult(true);
            // Accessing WaitHandle throws if Dispose tore down the source while this reader
            // was still using its token. The reader is held by a signal, not a scheduling delay.
            (await service.Subscription.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue();
        }
        finally
        {
            service.Subscription.Release.TrySetResult(true);
            await service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_Zero_Delay_Return_To_Waiting_Requires_A_Rate_And_A_Switch(bool rate, bool callback)
    {
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = callback ? (_, _) => Task.CompletedTask : null,
        });
        var activate = async () => await rig.Master.ActivateBitTimingAsync(0,
            bitsPerSecond: rate ? 250_000 : null, returnToWaiting: true);

        await activate.Should().ThrowAsync<ArgumentException>();
        rig.Service.Commands.Should().BeEmpty("validation must happen before the slave changes rate");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task A_Supplied_Bit_Rate_Requires_A_Callback_Before_Any_Activation(int delay, bool returnToWaiting)
    {
        using var rig = new ClockedLssRig();
        var activate = async () => await rig.Master.ActivateBitTimingAsync((ushort)delay, 500_000, returnToWaiting)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var error = await activate.Should().ThrowAsync<ArgumentException>();
        error.Which.ParamName.Should().Be("bitsPerSecond");
        rig.Service.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Submillisecond_Response_Window_Ends_Only_When_The_Clock_Reaches_It()
    {
        using var rig = new ClockedLssRig(new LssMasterOptions { ResponseTimeout = TimeSpan.FromTicks(1) });
        var inquire = rig.Master.InquireNodeIdAsync();
        await rig.ArmedAsync(TimeSpan.FromTicks(1));
        inquire.IsCompleted.Should().BeFalse();

        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));

        var result = async () => await inquire.WaitAsync(TimeSpan.FromSeconds(5));
        await result.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task A_Deadline_Near_The_Counter_Limit_Saturates_Instead_Of_Wrapping()
    {
        using var service = new LssTestService();
        service.Send = async frame =>
        {
            var payload = new byte[LssFrames.Length];
            payload[0] = frame.Data.Span[0];
            payload[1] = 0x42;
            await service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, payload, FrameFlags.None),
                false, TimeSpan.Zero, long.MaxValue)).WaitAsync(TimeSpan.FromSeconds(5));
            return new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = long.MaxValue - 1,
                HostTransmitTimestamp = long.MaxValue - 1,
            };
        };
        using var master = CanOpenLss.OpenMaster(service);

        (await master.InquireNodeIdAsync()).Should().Be(0x42);
    }

    [Fact]
    public async Task Caller_Cancellation_Wins_Over_Disposal_For_A_Queued_Command()
    {
        using var rig = new ClockedLssRig();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.Send = async frame =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return new TxConfirmation { Confirmed = true };
        };
        var first = rig.Master.InquireVendorIdAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var queued = rig.Master.InquireProductCodeAsync(caller.Token);
        try
        {
            // Disposal runs in the caller's cancellation notification, with the active gate
            // still held. The caller token is already cancelled before its queued wait ends.
            using var dispose = caller.Token.Register(rig.Master.Dispose);
            caller.Cancel();
            var queuedResult = async () => await queued.WaitAsync(TimeSpan.FromSeconds(5));
            await queuedResult.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            release.TrySetResult(true);
            var firstResult = async () => await first.WaitAsync(TimeSpan.FromSeconds(5));
            await firstResult.Should().ThrowAsync<ObjectDisposedException>();
        }
    }

    [Fact]
    public async Task Async_Disposal_Does_Not_Take_An_Injected_Timer_Actor()
    {
        using var rig = new ClockedLssRig();
        await rig.Master.DisposeAsync();

        (await rig.Timers.PostAsync(() => true)).Should().BeTrue();
    }

    [Fact]
    public async Task Fastscan_Does_Not_Send_The_Next_Probe_Before_The_Cycle_Gap_Ends()
    {
        var window = TimeSpan.FromMilliseconds(100);
        var gap = TimeSpan.FromMilliseconds(1);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            FastscanTimeout = window,
            FastscanCycleTime = gap,
        });
        rig.Add(new LssAddress(1, 0, 0, 0));
        var scan = rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
        });
        await rig.ArmedAsync(window);
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(window);
        await rig.ArmedAsync(gap);
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(gap - TimeSpan.FromTicks(1));
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));

        (await rig.RunAsync(scan)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fastscan_Returns_Without_A_Terminal_Gap_And_The_Next_Scan_Still_Waits(bool found)
    {
        var window = TimeSpan.FromMilliseconds(100);
        var gap = TimeSpan.FromSeconds(1);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            FastscanTimeout = window,
            FastscanCycleTime = gap,
        });
        if (found) rig.Add(new LssAddress(1, 0, 0, 0));
        var request = new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
        };

        var result = await rig.RunAsync(rig.Master.FastscanAsync(request));
        result.Succeeded.Should().Be(found);
        int probes = found ? 3 : 1;
        var expected = TimeSpan.FromTicks(window.Ticks * probes + gap.Ticks * (probes - 1));
        (rig.Clock.Elapsed - TimeSpan.FromSeconds(1)).Should().Be(expected);

        // The post-scan service is not a Fastscan probe and must not pay that gap.
        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        int commands = rig.Service.Commands.Count;
        using var cancel = new CancellationTokenSource();
        var next = rig.Master.FastscanAsync(request, cancel.Token);
        await rig.ArmedAsync(gap);
        rig.Service.Commands.Count.Should().Be(commands);
        await rig.Clock.AdvanceAsync(gap - TimeSpan.FromTicks(1));
        rig.Service.Commands.Count.Should().Be(commands);
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        await rig.ArmedAsync(window);
        rig.Service.Commands.Count.Should().Be(commands + 1);
        cancel.Cancel();
        var cancelled = async () => await next.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Activation_And_Waiting_Confirmations_Do_Not_Restart_The_Switch_Delay()
    {
        var switched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => { switched.TrySetResult(true); return Task.CompletedTask; },
        });
        var send = rig.Service.Send;
        rig.Service.Send = async frame =>
        {
            var confirmation = await send(frame);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(
                frame.Data.Span[0] == LssCommand.ActivateBitTiming ? 60 : 30));
            return confirmation;
        };

        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000, returnToWaiting: true);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(10));
        switched.Task.IsCompleted.Should().BeFalse();
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(9));
        switched.Task.IsCompleted.Should().BeFalse();
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await switched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
        (rig.Clock.Elapsed - TimeSpan.FromSeconds(1)).Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(250)]
    public async Task A_Late_Waiting_Confirmation_Resends_At_The_New_Rate_Despite_An_Early_Transmit_Stamp(int latency)
    {
        bool retuned = false;
        var waiting = new System.Collections.Generic.List<(TimeSpan At, bool Retuned)>();
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => { retuned = true; return Task.CompletedTask; },
        });
        var send = rig.Service.Send;
        rig.Service.Send = async frame =>
        {
            bool isWaiting = frame.Data.Span[0] == LssCommand.SwitchStateGlobal;
            if (isWaiting) waiting.Add((rig.Clock.Elapsed - TimeSpan.FromSeconds(1), retuned));
            var confirmation = await send(frame);
            // The driver's early stamp remains unchanged while confirmation arrives later.
            if (isWaiting && waiting.Count == 1)
                rig.Clock.Advance(TimeSpan.FromMilliseconds(latency));
            return confirmation;
        };

        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000, returnToWaiting: true);
        retuned.Should().BeTrue();
        if (latency < 200)
        {
            var remaining = TimeSpan.FromMilliseconds(200 - latency);
            await rig.ArmedAsync(remaining);
            activating.IsCompleted.Should().BeFalse();
            await rig.Clock.AdvanceAsync(remaining);
        }
        await activating.WaitAsync(TimeSpan.FromSeconds(5));

        waiting.Should().HaveCount(2);
        waiting[0].Retuned.Should().BeFalse();
        waiting[1].Retuned.Should().BeTrue();
        waiting[1].At.Should().Be(TimeSpan.FromMilliseconds(Math.Max(200, latency)));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(250)]
    public async Task A_Late_Activation_Confirmation_Waits_Only_The_Remaining_Absolute_Period(int latency)
    {
        bool switched = false;
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => { switched = true; return Task.CompletedTask; },
        });
        var send = rig.Service.Send;
        rig.Service.Send = async frame =>
        {
            var confirmation = await send(frame);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(latency));
            return confirmation;
        };

        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000);
        switched.Should().BeTrue("the first deadline already passed before confirmation");
        if (latency < 200)
        {
            await rig.ArmedAsync(TimeSpan.FromMilliseconds(200 - latency));
            activating.IsCompleted.Should().BeFalse();
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200 - latency));
        }
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
        (rig.Clock.Elapsed - TimeSpan.FromSeconds(1)).Should().Be(TimeSpan.FromMilliseconds(Math.Max(200, latency)));
        rig.Service.Send = send;
        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_Unstamped_Activation_Uses_The_Current_Clock_As_Its_Fallback()
    {
        using var rig = new ClockedLssRig();
        var send = rig.Service.Send;
        rig.Service.Send = async frame => (await send(frame)) with { HostTransmitTimestamp = 0 };

        var activating = rig.Master.ActivateBitTimingAsync(100);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_Deadline_Drains_Stamped_Replies_While_The_Background_Reader_Is_Parked(int offset, bool accepted)
    {
        using var service = new LssTestService();
        service.Subscription.ParkReader = true;
        Task? forwarded = null;
        service.Send = frame =>
        {
            var payload = new byte[LssFrames.Length];
            payload[0] = frame.Data.Span[0];
            payload[1] = 0x42;
            // Queue the frame before confirmation, without waiting for the parked reader.
            long arrival = 1000 + Stopwatch.Frequency + offset;
            forwarded = service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, payload, FrameFlags.None),
                false, TimeSpan.Zero, arrival));
            return Task.FromResult(new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = 500,
                HostTransmitTimestamp = 1000,
            });
        };
        using var master = CanOpenLss.OpenMaster(service);
        try
        {
            await service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (accepted)
                (await master.InquireNodeIdAsync()).Should().Be(0x42);
            else
            {
                var inquire = async () => await master.InquireNodeIdAsync();
                await inquire.Should().ThrowAsync<LssTimeoutException>();
            }
            await forwarded!.WaitAsync(TimeSpan.FromSeconds(5));
            service.Subscription.Finished.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            service.Subscription.Release.TrySetResult(true);
            await service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_Subscription_Pull_Failure_Is_Reported_As_A_Transport_Error()
    {
        using var service = new LssTestService();
        var failure = new InvalidOperationException("pull failed");
        service.Subscription.ReadFailure = failure;
        service.Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true });
        using var master = CanOpenLss.OpenMaster(service);

        var inquire = async () => await master.InquireNodeIdAsync();
        var error = await inquire.Should().ThrowAsync<CanOpenTransportException>();
        error.Which.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task A_Drained_Match_Survives_A_Subsequent_Subscription_Pull_Failure()
    {
        using var service = new LssTestService();
        service.Subscription.ReadFailure = new InvalidOperationException("pull failed after response");
        service.Subscription.FailAfterFirstRead = true;
        service.Send = frame =>
        {
            _ = service.Subscription.Enqueue(new CanFrameEvent(new CanFrameView(CanFrameType.Can20,
                (int)CanOpenCobId.LssSlaveMessage, LssFrames.Command(LssCommand.IdentifyNonConfiguredSlave),
                FrameFlags.None), false, TimeSpan.Zero));
            return Task.FromResult(new TxConfirmation { Confirmed = true });
        };
        using var master = CanOpenLss.OpenMaster(service);

        (await master.IdentifyNonConfiguredSlavesAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task A_Subscription_Failure_After_An_Unrelated_Reply_Preserves_The_Cause()
    {
        using var rig = new ClockedLssRig(parkReader: true);
        var failure = new InvalidOperationException("pull failed after unrelated reply");
        rig.Service.Subscription.ReadFailure = failure;
        rig.Service.Subscription.FailAfterFirstRead = true;
        rig.Service.Send = frame =>
        {
            _ = rig.Service.Subscription.Enqueue(new CanFrameEvent(new CanFrameView(CanFrameType.Can20,
                (int)CanOpenCobId.LssSlaveMessage, LssFrames.InquireNodeId(4), FrameFlags.None), false, TimeSpan.Zero));
            return Task.FromResult(new TxConfirmation { Confirmed = true });
        };
        try
        {
            var identify = async () => await rig.RunAsync(rig.Master.IdentifyNonConfiguredSlavesAsync());
            var error = await identify.Should().ThrowAsync<CanOpenTransportException>();
            error.Which.InnerException.Should().BeSameAs(failure);
        }
        finally
        {
            rig.Service.Subscription.Release.TrySetResult(true);
            await rig.Service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task An_Unstamped_Reply_Drained_At_Expiry_Does_Not_Answer_The_Next_Identify()
    {
        var window = TimeSpan.FromMilliseconds(100);
        using var rig = new ClockedLssRig(new LssMasterOptions { IdentificationTimeout = window }, parkReader: true);
        try
        {
            await rig.Service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = rig.Master.IdentifyNonConfiguredSlavesAsync();
            await rig.ArmedAsync(window);
            var forwarded = rig.Service.Subscription.Enqueue(new CanFrameEvent(new CanFrameView(CanFrameType.Can20,
                (int)CanOpenCobId.LssSlaveMessage, LssFrames.Command(LssCommand.IdentifyNonConfiguredSlave),
                FrameFlags.None), false, TimeSpan.Zero));
            await rig.Clock.AdvanceAsync(window);
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            await forwarded.WaitAsync(TimeSpan.FromSeconds(5));

            var next = rig.Master.IdentifyNonConfiguredSlavesAsync();
            await rig.ArmedAsync(window);
            await rig.Clock.AdvanceAsync(window);
            (await next.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }
        finally
        {
            rig.Service.Subscription.Release.TrySetResult(true);
            await rig.Service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_Unstamped_Reply_Buffered_Before_A_Request_Does_Not_Answer_It(bool selective, bool parkReader)
    {
        using var rig = new ClockedLssRig(parkReader: parkReader);
        try
        {
            Func<Task<bool>> request = selective
                ? () => rig.Master.SwitchStateSelectiveAsync(new LssAddress(1, 2, 3, 4))
                : () => rig.Master.IdentifyNonConfiguredSlavesAsync();
            (await rig.RunAsync(request())).Should().BeFalse();
            byte command = selective ? LssCommand.SwitchSelectiveAcknowledge : LssCommand.IdentifyNonConfiguredSlave;
            CanFrameEvent Reply() => new(new CanFrameView(CanFrameType.Can20,
                (int)CanOpenCobId.LssSlaveMessage, LssFrames.Command(command), FrameFlags.None), false, TimeSpan.Zero);
            var stale = rig.Service.Subscription.Enqueue(Reply());
            if (!parkReader) await stale.WaitAsync(TimeSpan.FromSeconds(5));

            (await rig.RunAsync(request())).Should().BeFalse("the queued frame predates the request");
            await stale.WaitAsync(TimeSpan.FromSeconds(5));

            // Missing timestamps remain supported for a fresh reply produced during the send.
            Task? delivered = null;
            rig.Service.Send = frame =>
            {
                if (frame.Data.Span[0] == (selective ? LssCommand.SwitchSelectiveSerial : LssCommand.IdentifyNonConfiguredRemote))
                    delivered = rig.Service.Subscription.Enqueue(Reply());
                return Task.FromResult(new TxConfirmation { Confirmed = true });
            };
            (await rig.RunAsync(request())).Should().BeTrue();
            await delivered!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (parkReader)
            {
                rig.Service.Subscription.Release.TrySetResult(true);
                await rig.Service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Theory]
    [InlineData(150, 0, false)]
    [InlineData(250, 0, false)]
    [InlineData(0, 150, false)]
    [InlineData(0, 150, true)]
    [InlineData(0, 0, true)]
    public async Task A_Missed_Old_Rate_Waiting_Frame_Is_Sent_After_Retuning(
        int activationLatency, int waitingDriverLatency, bool unstampedWaiting)
    {
        bool retuned = false;
        var waiting = new System.Collections.Generic.List<(TimeSpan At, bool Retuned)>();
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => { retuned = true; return Task.CompletedTask; },
        });
        var send = rig.Service.Send;
        rig.Service.Send = async frame =>
        {
            bool isWaiting = frame.Data.Span[0] == LssCommand.SwitchStateGlobal;
            if (isWaiting && waiting.Count == 0)
                rig.Clock.Advance(TimeSpan.FromMilliseconds(waitingDriverLatency));
            if (isWaiting)
                waiting.Add((rig.Clock.Elapsed - TimeSpan.FromSeconds(1), retuned));
            var confirmation = await send(frame);
            if (!isWaiting)
                rig.Clock.Advance(TimeSpan.FromMilliseconds(activationLatency));
            return isWaiting && unstampedWaiting ? confirmation with { HostTransmitTimestamp = 0 } : confirmation;
        };

        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000, returnToWaiting: true);
        bool missed = activationLatency >= 100 || waitingDriverLatency >= 100;
        if (missed)
        {
            retuned.Should().BeTrue();
            int remaining = 200 - Math.Max(activationLatency, waitingDriverLatency);
            if (remaining > 0)
            {
                await rig.ArmedAsync(TimeSpan.FromMilliseconds(remaining));
                await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(remaining));
            }
        }
        else
        {
            await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
            await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        }
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
        waiting.Should().NotBeEmpty();
        if (missed)
        {
            waiting[waiting.Count - 1].Retuned.Should().BeTrue();
            waiting[waiting.Count - 1].At.Should().Be(TimeSpan.FromMilliseconds(Math.Max(200, Math.Max(activationLatency, waitingDriverLatency))));
        }
        else
        {
            waiting.Should().ContainSingle();
            waiting[0].Retuned.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_Missed_Waiting_Window_Without_A_Host_Switch_Is_A_Transport_Error(
        bool callback, bool lateWaiting)
    {
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = callback ? (_, _) => Task.CompletedTask : null,
        });
        var send = rig.Service.Send;
        rig.Service.Send = async frame =>
        {
            if (lateWaiting && frame.Data.Span[0] == LssCommand.SwitchStateGlobal)
                rig.Clock.Advance(TimeSpan.FromMilliseconds(150));
            var confirmation = await send(frame);
            if (!lateWaiting)
                rig.Clock.Advance(TimeSpan.FromMilliseconds(150));
            return confirmation;
        };

        var activating = rig.Master.ActivateBitTimingAsync(100,
            returnToWaiting: true);
        activating.IsCompleted.Should().BeFalse("the slaves are still in their silent period");
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(50));
        var following = rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        int sent = rig.Service.Commands.Count;
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50) - TimeSpan.FromTicks(1));
        rig.Service.Commands.Count.Should().Be(sent, "the gate must stay held during the silent period");
        activating.IsCompleted.Should().BeFalse();
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        var activate = async () => await activating.WaitAsync(TimeSpan.FromSeconds(5));
        await activate.Should().ThrowAsync<CanOpenTransportException>();
        await following.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Service.Commands.Count.Should().Be(sent + 1);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    public async Task Fatal_Subscription_Failures_Are_Not_Converted_To_Transport_Errors(Type errorType)
    {
        using var service = new LssTestService();
        var failure = (Exception)Activator.CreateInstance(errorType)!;
        service.Subscription.ReadFailure = failure;
        service.Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true });
        using var master = CanOpenLss.OpenMaster(service);

        var inquire = async () => await master.InquireNodeIdAsync();
        var thrown = await inquire.Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(failure);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    public async Task Fatal_Background_Read_Failures_Keep_Their_Original_Exception(Type errorType)
    {
        using var service = new LssTestService();
        var failure = (Exception)Activator.CreateInstance(errorType)!;
        service.Subscription.WaitFailure = failure;
        service.Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true });
        using var master = CanOpenLss.OpenMaster(service);
        await service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var inquire = async () => await master.InquireNodeIdAsync();
        var thrown = await inquire.Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(failure);
    }

}
