using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>Opens an <see cref="ILssMaster"/> on a bus. Non-instantiable.</summary>
public static class CanOpenLss
{
    /// <summary>
    /// Opens a master that owns a private <see cref="CanBusService"/> around <paramref name="bus"/>.
    /// Disposing the master disposes that service; the bus itself is not disposed.
    /// </summary>
    public static ILssMaster OpenMaster(ICanBus bus, LssMasterOptions? options = null)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        var resolved = options ?? new LssMasterOptions();
        resolved.Validate();
        var service = new CanBusService(bus);
        // leaveOpen false: a failure while the master is constructed disposes this private service.
        return OpenValidated(service, resolved, leaveOpen: false);
    }

    /// <summary>
    /// Opens a master on an existing <paramref name="service"/>, shared with other protocols on
    /// the same bus. With <paramref name="leaveOpen"/> <see langword="true"/> (the default)
    /// disposing the master does not dispose the service. An LSS slave opened on the same service
    /// with <see cref="CanOpen.OpenNode(ICanBusService, byte, CanOpenNodeOptions?, bool)"/> is heard: the master receives frames the adapter flags as
    /// this host's echo, which is how such a slave's replies arrive.
    /// </summary>
    public static ILssMaster OpenMaster(ICanBusService service, LssMasterOptions? options = null, bool leaveOpen = true)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        var resolved = options ?? new LssMasterOptions();
        resolved.Validate();
        return OpenValidated(service, resolved, leaveOpen);
    }

    private static ILssMaster OpenValidated(ICanBusService service, LssMasterOptions options, bool leaveOpen)
    {
        try
        {
            return new LssMaster(service, options, ownsService: !leaveOpen);
        }
        catch
        {
            // Validation already ran, so this is a failure after the master took ownership.
            if (!leaveOpen) service.Dispose();
            throw;
        }
    }
}

/// <summary>The <see cref="ILssMaster"/> implementation.</summary>
internal sealed partial class LssMaster : ILssMaster
{
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(2);
    private readonly ICanBusService _service;
    private readonly bool _ownsService;
    private readonly ProtocolActor? _timers;
    private readonly ITimeSource _time;
    private readonly ISubscription _subscription;
    private readonly object _receiveGate = new();
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<Inbound> _inbound = Channel.CreateBounded<Inbound>(new BoundedChannelOptions(
        CanBusService.DefaultBufferCapacity)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly CancellationTokenSource _lifetime = new();

    private int _disposed;
    // Guarded by _gate. Interrupted requests retain response-window/silence reservations.
    private long _nextCommandAt;
    private Exception? _indeterminateSend;

    internal LssMaster(ICanBusService service, LssMasterOptions options, bool ownsService, ProtocolActor? timers = null)
    {
        _service = service;
        Options = options;
        _ownsService = ownsService;
        // Production uses system timers on Stopwatch's monotonic counter, without an actor
        // thread per master. An injected actor shares its clock with the test transport stamps.
        _timers = timers;
        _time = timers is null ? MonotonicTimeSource.Instance : timers.TimeSource;
        try
        {
            // Echoes included (L-2, #359): a CanOpenNode LSS slave in this process on the same
            // bus or service is on the bus too, and an adapter marks its replies as this host's
            // echoes. The master itself only transmits on 7E5h, so an echo on 7E4h can only be
            // a slave's reply, never one of the master's own requests.
            _subscription = service.Subscribe(frame =>
            {
                var can = frame.Frame;
                return can.FrameKind == CanFrameType.Can20 && !can.IsErrorFrame && !can.IsRemoteFrame
                    && !can.IsExtendedFrame && (uint)can.ID == CanOpenCobId.LssSlaveMessage;
            }, includeEcho: true);
        }
        catch
        {
            // Subscribe failed before the reader task existed. Cancel and dispose what this
            // constructor already created so the caller can open again on the same service.
            _lifetime.Cancel();
            _readerCts.Cancel();
            _inbound.Writer.TryComplete();
            _readerCts.Dispose();
            _lifetime.Dispose();
            _gate.Dispose();
            throw;
        }
        _readerTask = Task.Run(ReadAsync);
    }

    /// <inheritdoc />
    public LssMasterOptions Options { get; }

    /// <inheritdoc />
    public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
    {
        if (state is not (LssState.Waiting or LssState.Configuration))
            throw new ArgumentOutOfRangeException(nameof(state), state, "LSS state is waiting or configuration.");
        return Execute(async ct =>
        {
            await SendAsync(LssFrames.SwitchGlobal(state), ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveVendor, address.VendorId), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveProduct, address.ProductCode), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveRevision, address.RevisionNumber), ct)
                .ConfigureAwait(false);
            var sent = await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveSerial, address.SerialNumber), ct)
                .ConfigureAwait(false);
            var response = await ReceiveAsync(sent, static d => d.Span[0] == LssCommand.SwitchSelectiveAcknowledge,
                Options.ResponseTimeout, ct).ConfigureAwait(false);
            return response is not null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
        => ConfirmedAsync(LssFrames.ConfigureNodeId(nodeId), LssCommand.ConfigureNodeId, cancellationToken);

    /// <inheritdoc />
    public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
        CancellationToken cancellationToken = default)
        => ConfirmedAsync(LssFrames.ConfigureBitTiming(tableSelector, tableIndex), LssCommand.ConfigureBitTiming,
            cancellationToken);

    /// <inheritdoc />
    public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
        bool returnToWaiting = false, CancellationToken cancellationToken = default)
    {
        if (bitsPerSecond is not null && Options.BitRateSwitch is null)
            throw new ArgumentException("Specifying a bit rate requires a BitRateSwitch callback.", nameof(bitsPerSecond));
        if (switchDelayMilliseconds == 0 && returnToWaiting && bitsPerSecond is null)
            throw new ArgumentException("Returning to waiting after a zero delay requires a bit rate and BitRateSwitch callback.");
        return Execute(async ct =>
        {
            var delay = TimeSpan.FromMilliseconds(switchDelayMilliseconds);
            var time = _time;
            var sent = await SendAsync(LssFrames.ActivateBitTiming(switchDelayMilliseconds), ct)
                .ConfigureAwait(false);
            long start = sent.Transmit > 0 ? sent.Transmit : time.GetTimestamp();
            long switchAt = TickMath.DueAt(start, delay, time.Frequency);
            long resumeAt = TickMath.DueAt(switchAt, delay, time.Frequency);
            Reserve(resumeAt);
            // CiA 305 §6.4.4 and Figure 15: nothing is sent during either switch_delay period. A
            // host that switches with the slaves sends waiting after both, on the new rate. A
            // host that cannot switch would be left talking at the old rate, so it keeps the
            // documented departure: waiting goes out before the first period ends (L-5, #359).
            var waiting = !returnToWaiting ? WaitingPhase.None
                : delay == TimeSpan.Zero ? WaitingPhase.AfterSwitch
                : bitsPerSecond is not null || time.GetTimestamp() >= switchAt ? WaitingPhase.AfterResume
                : WaitingPhase.BeforeDelay;

            async Task SendWaitingAtAsync(WaitingPhase phase)
            {
                if (waiting != phase) return;
                if (phase == WaitingPhase.AfterResume && bitsPerSecond is null)
                    throw new CanOpenTransportException(
                        "The waiting frame could not be verified before the old-rate deadline. Both switch-delay periods have elapsed; a host bit-rate switch is required to return the slaves to waiting.");
                await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
                // A driver stamp is not wire completion. A confirmation at or after the first
                // deadline may have missed the old rate; without a host switch the call then
                // fails once both periods have elapsed.
                if (phase == WaitingPhase.BeforeDelay && time.GetTimestamp() >= switchAt)
                    waiting = WaitingPhase.AfterResume;
            }

            await SendWaitingAtAsync(WaitingPhase.BeforeDelay).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
                await DelayUntilAsync(switchAt, ct).ConfigureAwait(false);
            if (bitsPerSecond is int rate)
                await Options.BitRateSwitch!(rate, ct).ConfigureAwait(false);
            await SendWaitingAtAsync(WaitingPhase.AfterSwitch).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
                await DelayUntilAsync(resumeAt, ct).ConfigureAwait(false);
            await SendWaitingAtAsync(WaitingPhase.AfterResume).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
        => ConfirmedAsync(LssFrames.Command(LssCommand.StoreConfiguration), LssCommand.StoreConfiguration,
            cancellationToken);

    /// <inheritdoc />
    public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default)
        => InquireU32Async(LssCommand.InquireVendorId, cancellationToken);

    /// <inheritdoc />
    public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default)
        => InquireU32Async(LssCommand.InquireProductCode, cancellationToken);

    /// <inheritdoc />
    public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default)
        => InquireU32Async(LssCommand.InquireRevisionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default)
        => InquireU32Async(LssCommand.InquireSerialNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
    {
        // One gate hold for the four reads, so another caller cannot select a different slave
        // between them.
        uint vendor = 0, product = 0, revision = 0, serial = 0;
        await Execute(async ct =>
        {
            vendor = await InquireU32CoreAsync(LssCommand.InquireVendorId, ct).ConfigureAwait(false);
            product = await InquireU32CoreAsync(LssCommand.InquireProductCode, ct).ConfigureAwait(false);
            revision = await InquireU32CoreAsync(LssCommand.InquireRevisionNumber, ct).ConfigureAwait(false);
            serial = await InquireU32CoreAsync(LssCommand.InquireSerialNumber, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new LssAddress(vendor, product, revision, serial);
    }

    /// <inheritdoc />
    public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            var sent = await SendAsync(LssFrames.Command(LssCommand.InquireNodeId), ct)
                .ConfigureAwait(false);
            var response = await ReceiveRequiredAsync(sent, LssCommand.InquireNodeId, ct).ConfigureAwait(false);
            return response[1];
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteVendor, range.VendorId), ct).ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteProduct, range.ProductCode), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionLow, range.RevisionLow), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionHigh, range.RevisionHigh), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteSerialLow, range.SerialLow), ct)
                .ConfigureAwait(false);
            var sent = await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteSerialHigh, range.SerialHigh), ct)
                .ConfigureAwait(false);
            // The whole identification window is occupied, so a later slave is not left
            // queued for the next command.
            var response = await ReceiveAsync(sent, static d => d.Span[0] == LssCommand.IdentifySlave,
                Options.IdentificationTimeout, ct, drain: true).ConfigureAwait(false);
            return response is not null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            var sent = await SendAsync(LssFrames.Command(LssCommand.IdentifyNonConfiguredRemote), ct)
                .ConfigureAwait(false);
            var response = await ReceiveAsync(sent, static d => d.Span[0] == LssCommand.IdentifyNonConfiguredSlave,
                Options.IdentificationTimeout, ct, drain: true).ConfigureAwait(false);
            return response is not null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken = default)
    {
        var payload = LssFrames.Manufacturer(commandSpecifier, parameters.Span);
        return Execute(async ct =>
        {
            var sent = await SendAsync(payload, ct).ConfigureAwait(false);
            return await ReceiveAsync(sent, d => d.Span[0] == commandSpecifier, Options.ResponseTimeout, ct)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var reader = BeginDispose();
        if (reader is null) return;
        try
        {
            reader.Wait(ReaderShutdownTimeout);
        }
        catch (AggregateException)
        {
            // A faulted reader has already completed the inbound channel.
        }
        FinishDispose(reader);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var reader = BeginDispose();
        if (reader is null) return;
        using var timeout = new CancellationTokenSource();
        var limit = Task.Delay(ReaderShutdownTimeout, timeout.Token);
        await Task.WhenAny(reader, limit).ConfigureAwait(false);
        timeout.Cancel();
        FinishDispose(reader);
    }

    private void FinishDispose(Task reader)
    {
        if (_ownsService) _service.Dispose();
        // A parked reader keeps its sources alive. Release them and observe any fault when
        // it eventually exits, including when it outlasted the bounded disposal wait.
        _ = reader.ContinueWith(static (completed, state) =>
        {
            var master = (LssMaster)state!;
            _ = completed.Exception;
            master._readerCts.Dispose();
            master._lifetime.Dispose();
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels the reader and completes the inbound channel. Returns null when disposal already
    /// started. The gate stays undisposed: disposing a <see cref="SemaphoreSlim"/> does not release a
    /// waiter, and a command that already passed <c>WaitAsync</c> still has to release it.
    /// </summary>
    private Task? BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return null;
        _lifetime.Cancel();
        _readerCts.Cancel();
        _subscription.Dispose();
        _inbound.Writer.TryComplete();
        return _readerTask;
    }

    private Task<LssServiceResult> ConfirmedAsync(byte[] request, byte commandSpecifier, CancellationToken cancellationToken)
        => Execute(async ct =>
        {
            var sent = await SendAsync(request, ct).ConfigureAwait(false);
            var response = await ReceiveRequiredAsync(sent, commandSpecifier, ct).ConfigureAwait(false);
            return new LssServiceResult(response[1], response[2]);
        }, cancellationToken);

    private Task<uint> InquireU32Async(byte commandSpecifier, CancellationToken cancellationToken)
        => Execute(ct => InquireU32CoreAsync(commandSpecifier, ct), cancellationToken);

    private async Task<uint> InquireU32CoreAsync(byte commandSpecifier, CancellationToken cancellationToken)
    {
        var sent = await SendAsync(LssFrames.Command(commandSpecifier), cancellationToken)
            .ConfigureAwait(false);
        var response = await ReceiveRequiredAsync(sent, commandSpecifier, cancellationToken).ConfigureAwait(false);
        return LssFrames.ReadU32(response);
    }

    private async Task<T> Execute<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                linked.Token.ThrowIfCancellationRequested();
                if (_indeterminateSend is { } failure)
                    throw new CanOpenTransportException(
                        "The LSS master cannot continue after an indeterminate transmission. Reset the transport and re-establish a quiescent network before opening another master.", failure);
                await DelayUntilAsync(_nextCommandAt, linked.Token).ConfigureAwait(false);
                return await body(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0
                                                  && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(LssMaster));
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(LssMaster));
        }
    }

    private Task Execute(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
        => Execute(async ct =>
        {
            await body(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private void Reserve(long until) => _nextCommandAt = Math.Max(_nextCommandAt, until);

    private async Task<Sent> SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.LssMasterMessage), payload, isExtendedFrame: false);
        lock (_receiveGate)
        {
            // Nothing already buffered can answer this request, even without arrival stamps.
            // Serialize the discard with dequeue/forwarding by the background reader.
            ForwardAvailable();
            while (_inbound.Reader.TryRead(out _)) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        bool mayHaveTransmitted = true;
        try
        {
            var confirmation = await _service.SendConfirmedAsync(frame, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!confirmation.Confirmed)
            {
                mayHaveTransmitted = confirmation.FailureReason != TxConfirmFailureReason.Rejected;
                throw new CanOpenTransportException(
                    $"LSS master frame 0x{payload[0]:X2} was not confirmed: {confirmation.FailureReason}.");
            }
            return new Sent(confirmation.HostHandoffTimestamp, confirmation.HostTransmitTimestamp);
        }
        catch (Exception error) when (mayHaveTransmitted)
        {
            // A timed-out/cancelled driver may still transmit arbitrarily later. No finite
            // quiet period makes another request safe, so this master must never send again.
            _indeterminateSend = error;
            throw;
        }
    }

    private async Task<byte[]?> ReceiveAsync(Sent sent, Func<ReadOnlyMemory<byte>, bool> match, TimeSpan timeout,
        CancellationToken cancellationToken, bool drain = false)
    {
        var time = _time;
        long start = sent.Transmit > 0 ? sent.Transmit : time.GetTimestamp();
        long deadline = TickMath.DueAt(start, timeout, time.Frequency);
        byte[]? first = null;
        using var timeoutSource = _timers is { } timers
            ? new ClockTimeout(timers, deadline)
            : new ClockTimeout(RemainingUntil(deadline));
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TakeAvailable(sent, deadline, match, ref first, stopAtFirst: !drain);
                if ((!drain && first is not null) || time.GetTimestamp() >= deadline)
                    return first;
                try
                {
                    if (!await _inbound.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        if (first is not null) return first;
                        ThrowIfDisposed();
                        throw ReaderEnded();
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                {
                    // A system timer can fire early. Finish the absolute wait without spinning
                    // on a cancelled token, then drain at the same loop head.
                    await DelayUntilAsync(deadline, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (CanOpenTransportException)
        {
            if (first is not null) return first;
            Reserve(deadline);
            ThrowIfDisposed();
            throw;
        }
        catch (OperationCanceledException)
        {
            // A later handoff cannot distinguish a delayed reply to this abandoned request.
            Reserve(deadline);
            throw;
        }
    }

    private async Task DelayUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        if (_timers is { } timers)
        {
            await ClockTimeout.WaitUntilAsync(timers, deadline, cancellationToken).ConfigureAwait(false);
            return;
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = RemainingUntil(deadline);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan RemainingUntil(long deadline)
    {
        var remaining = TickMath.RemainingFromTicks(Math.Max(0, deadline - _time.GetTimestamp()), _time.Frequency);
        // System timers take whole milliseconds on every target, including net48. Round up,
        // and bound a single wait; the next iteration rechecks the absolute monotonic deadline.
        return TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds)));
    }

    private static CanOpenTransportException ReaderEnded(Exception? inner = null)
    {
        const string message = "The LSS master reader ended before a slave answered.";
        return inner is null ? new CanOpenTransportException(message) : new CanOpenTransportException(message, inner);
    }

    private void TakeAvailable(Sent sent, long deadline, Func<ReadOnlyMemory<byte>, bool> match, ref byte[]? first,
        bool stopAtFirst)
    {
        lock (_receiveGate)
        {
            CanOpenTransportException? readError = null;
            try
            {
                ForwardAvailable();
            }
            catch (CanOpenTransportException ex)
            {
                readError = ex;
            }
            while (_inbound.Reader.TryRead(out var item))
            {
                if (!InWindow(sent, item.Arrival, deadline)) continue;
                if (!match(item.Data)) continue;
                first ??= item.Data.ToArray();
                if (stopAtFirst) return;
            }
            if (readError is not null && first is null) throw readError;
        }
    }

    // Both consumers hold _receiveGate from dequeue through forwarding. At a deadline the
    // command can drain the subscription itself without overtaking a frame held by the reader.
    private void ForwardAvailable()
    {
        try
        {
            while (_subscription.TryRead(out var frameEvent))
            {
                var data = frameEvent.Frame.Data;
                if (data.Length != LssFrames.Length) continue;
                _ = _inbound.Writer.TryWrite(new Inbound(frameEvent.HostArrivalTimestamp, data));
            }
        }
        catch (Exception ex) when (!IsFatalReadFailure(ex))
        {
            var error = ReaderEnded(ex);
            _inbound.Writer.TryComplete(error);
            _readerCts.Cancel();
            throw error;
        }
    }

    private static bool IsFatalReadFailure(Exception error)
        => error is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static bool InWindow(Sent sent, long arrival, long deadline)
    {
        if (arrival > deadline) return false;
        // An unstamped frame has no lower bound. Zero is not "before this request".
        if (arrival <= 0) return true;
        return sent.Handoff <= 0 || arrival >= sent.Handoff;
    }

    private async Task<byte[]> ReceiveRequiredAsync(Sent sent, byte commandSpecifier, CancellationToken cancellationToken)
    {
        var response = await ReceiveAsync(sent, d => d.Span[0] == commandSpecifier, Options.ResponseTimeout,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            ThrowIfDisposed();
            throw new LssTimeoutException(commandSpecifier, Options.ResponseTimeout);
        }
        return response;
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _subscription.WaitToReadAsync(_readerCts.Token).ConfigureAwait(false))
            {
                lock (_receiveGate) ForwardAvailable();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancels the reader. Completing this task is what lets Dispose finish.
        }
        catch (Exception ex)
        {
            var error = ex as CanOpenTransportException ?? (IsFatalReadFailure(ex) ? ex : ReaderEnded(ex));
            // ForwardAvailable may already have completed the channel with this same error.
            _inbound.Writer.TryComplete(error);
            throw;
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(LssMaster));
    }

    private enum WaitingPhase { None, BeforeDelay, AfterSwitch, AfterResume }

    private readonly struct Sent
    {
        public Sent(long handoff, long transmit)
        {
            Handoff = handoff;
            Transmit = transmit;
        }

        public long Handoff { get; }
        public long Transmit { get; }
    }

    private readonly struct Inbound
    {
        public Inbound(long arrival, ReadOnlyMemory<byte> data)
        {
            Arrival = arrival;
            Data = data;
        }

        public long Arrival { get; }
        public ReadOnlyMemory<byte> Data { get; }
    }
}
