using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
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
    /// disposing the master does not dispose the service.
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
    private readonly ICanBusService _service;
    private readonly bool _ownsService;
    private readonly ProtocolActor _timers;
    private readonly bool _ownsTimers;
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

    internal LssMaster(ICanBusService service, LssMasterOptions options, bool ownsService, ProtocolActor? timers = null)
    {
        _service = service;
        Options = options;
        _ownsService = ownsService;
        // Production transport stamps and the scheduler share Stopwatch's monotonic counter.
        // An injected actor must share its clock with the test transport's stamps as well.
        _timers = timers ?? new ProtocolActor();
        _ownsTimers = timers is null;
        try
        {
            _subscription = service.Subscribe(frame =>
            {
                var can = frame.Frame;
                return !can.IsExtendedFrame && (uint)can.ID == CanOpenCobId.LssSlaveMessage;
            });
        }
        catch
        {
            // Subscribe failed before the reader task existed. Cancel and dispose what this
            // constructor already created so the caller can open again on the same service.
            if (_ownsTimers) _timers.Dispose();
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
        if (switchDelayMilliseconds == 0 && returnToWaiting
            && (bitsPerSecond is null || Options.BitRateSwitch is null))
            throw new ArgumentException("Returning to waiting after a zero delay requires a bit rate and BitRateSwitch callback.");
        return Execute(async ct =>
        {
            var sent = await SendAsync(LssFrames.ActivateBitTiming(switchDelayMilliseconds), ct).ConfigureAwait(false);
            var delay = TimeSpan.FromMilliseconds(switchDelayMilliseconds);
            var time = _timers.TimeSource;
            long start = sent.Transmit > 0 ? sent.Transmit : time.GetTimestamp();
            long switchAt = TickMath.DueAt(start, delay, time.Frequency);
            long resumeAt = TickMath.DueAt(switchAt, delay, time.Frequency);
            bool waitingAtNewRate = returnToWaiting && delay == TimeSpan.Zero;
            if (returnToWaiting && delay > TimeSpan.Zero)
            {
                if (time.GetTimestamp() >= switchAt)
                    waitingAtNewRate = true;
                else
                {
                    var waiting = await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
                    long transmitted = waiting.Transmit > 0 ? waiting.Transmit : time.GetTimestamp();
                    waitingAtNewRate = transmitted >= switchAt;
                }
            }
            if (delay > TimeSpan.Zero)
                await DelayUntilAsync(switchAt, ct).ConfigureAwait(false);
            if (bitsPerSecond is int rate && Options.BitRateSwitch is { } switchRate)
                await switchRate(rate, ct).ConfigureAwait(false);
            if (waitingAtNewRate && delay == TimeSpan.Zero)
                await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
                await DelayUntilAsync(resumeAt, ct).ConfigureAwait(false);
            if (waitingAtNewRate && (bitsPerSecond is null || Options.BitRateSwitch is null))
                throw new CanOpenTransportException(
                    "The waiting frame could not be verified before the old-rate deadline. Both switch-delay periods have elapsed; a host bit-rate switch is required to return the slaves to waiting.");
            if (waitingAtNewRate && delay > TimeSpan.Zero)
                await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
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
            var sent = await SendAsync(LssFrames.Command(LssCommand.InquireNodeId), ct).ConfigureAwait(false);
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
            reader.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // A faulted reader has already completed the inbound channel.
        }
        if (_ownsService) _service.Dispose();
        if (_ownsTimers) _timers.Dispose();
        // A reader still parked after the wait must not see its token source disposed.
        if (!reader.IsCompleted) return;
        _readerCts.Dispose();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var reader = BeginDispose();
        if (reader is null) return;
        // WhenAny observes completion, including a fault. Reading Exception marks that fault
        // observed; a catch here would hide which failure ended the reader.
        await Task.WhenAny(reader).ConfigureAwait(false);
        if (reader.IsFaulted)
            _ = reader.Exception;
        if (_ownsService) _service.Dispose();
        if (_ownsTimers) _timers.Dispose();
        _readerCts.Dispose();
        _lifetime.Dispose();
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
        var sent = await SendAsync(LssFrames.Command(commandSpecifier), cancellationToken).ConfigureAwait(false);
        var response = await ReceiveRequiredAsync(sent, commandSpecifier, cancellationToken).ConfigureAwait(false);
        return LssFrames.ReadU32(response);
    }

    private async Task<T> Execute<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0
                                                  && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(LssMaster));
        }

        try
        {
            ThrowIfDisposed();
            return await body(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0
                                                  && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(LssMaster));
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Execute(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
        => Execute(async ct =>
        {
            await body(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<Sent> SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        using var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.LssMasterMessage), payload, isExtendedFrame: false);
        lock (_receiveGate)
        {
            // Nothing already buffered can answer this request, even without arrival stamps.
            // Serialize the discard with dequeue/forwarding by the background reader.
            ForwardAvailable();
            while (_inbound.Reader.TryRead(out _)) { }
        }
        var confirmation = await _service.SendConfirmedAsync(frame, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!confirmation.Confirmed)
            throw new CanOpenTransportException(
                $"LSS master frame 0x{payload[0]:X2} was not confirmed: {confirmation.FailureReason}.");
        return new Sent(confirmation.HostHandoffTimestamp, confirmation.HostTransmitTimestamp);
    }

    private async Task<byte[]?> ReceiveAsync(Sent sent, Func<ReadOnlyMemory<byte>, bool> match, TimeSpan timeout,
        CancellationToken cancellationToken, bool drain = false)
    {
        var time = _timers.TimeSource;
        long start = sent.Transmit > 0 ? sent.Transmit : time.GetTimestamp();
        long deadline = TickMath.DueAt(start, timeout, time.Frequency);
        byte[]? first = null;
        cancellationToken.ThrowIfCancellationRequested();
        TakeAvailable(sent, deadline, match, ref first, stopAtFirst: !drain);
        if ((!drain && first is not null) || time.GetTimestamp() >= deadline)
            return first;
        using var timeoutSource = new ClockTimeout(_timers, deadline);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
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
                TakeAvailable(sent, deadline, match, ref first, stopAtFirst: !drain);
                return first;
            }
            catch (CanOpenTransportException)
            {
                if (first is not null) return first;
                ThrowIfDisposed();
                throw;
            }
        }
    }

    private Task DelayUntilAsync(long deadline, CancellationToken cancellationToken)
        => ClockTimeout.WaitUntilAsync(_timers, deadline, cancellationToken);

    private static CanOpenTransportException ReaderEnded()
    {
        return new CanOpenTransportException("The LSS master reader ended before a slave answered.");
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
            var error = new CanOpenTransportException("The LSS master reader ended before a slave answered.", ex);
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
            _inbound.Writer.TryComplete(IsFatalReadFailure(ex) ? ex : new CanOpenTransportException(
                "The LSS master reader ended before a slave answered.", ex));
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
