using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
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
        try
        {
            return new LssMaster(service, resolved, ownsService: true);
        }
        catch
        {
            service.Dispose();
            throw;
        }
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
        try
        {
            return new LssMaster(service, resolved, ownsService: !leaveOpen);
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
    private readonly ISubscription _subscription;
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

    internal LssMaster(ICanBusService service, LssMasterOptions options, bool ownsService)
    {
        _service = service;
        Options = options;
        options.Validate();
        _ownsService = ownsService;
        ISubscription? subscribed = null;
        Task? reader = null;
        try
        {
            subscribed = service.Subscribe(frame =>
            {
                var can = frame.Frame;
                return !can.IsExtendedFrame && (uint)can.ID == CanOpenCobId.LssSlaveMessage;
            });
            _subscription = subscribed;
            reader = Task.Run(ReadAsync);
            _readerTask = reader;
        }
        catch
        {
            _lifetime.Cancel();
            _readerCts.Cancel();
            subscribed?.Dispose();
            _inbound.Writer.TryComplete();
            if (reader is not null)
            {
                try
                {
                    reader.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // The reader stops because construction was abandoned.
                }
            }
            _readerCts.Dispose();
            _lifetime.Dispose();
            _gate.Dispose();
            throw;
        }
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
        => Execute(async ct =>
        {
            await SendAsync(LssFrames.ActivateBitTiming(switchDelayMilliseconds), ct).ConfigureAwait(false);
            var delay = TimeSpan.FromMilliseconds(switchDelayMilliseconds);
            // A positive delay leaves the slave on the current rate, so waiting still reaches it.
            // A zero delay applies the new rate inside that activate frame. Waiting has to follow
            // this master's switch, or it goes out at the rate the slave just left.
            if (returnToWaiting && delay > TimeSpan.Zero)
                await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            if (bitsPerSecond is int rate && Options.BitRateSwitch is { } switchRate)
                await switchRate(rate, ct).ConfigureAwait(false);
            if (returnToWaiting && delay == TimeSpan.Zero)
                await SendAsync(LssFrames.SwitchGlobal(LssState.Waiting), ct).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }, cancellationToken);

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
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
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
        // The deadline starts when the frame was handed to the driver, not when this wait
        // begins: a response already queued can still be late (TxConfirmation.HostTransmitTimestamp).
        // A confirmation that carries no stamp is not an instant of zero: the wait starts now.
        // A drain keeps the first match and stays until the deadline, which is how Fastscan and
        // identify consume every slave that answered this request before the next one is sent.
        long start = sent.Transmit > 0 ? sent.Transmit : Stopwatch.GetTimestamp();
        long deadline = start + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        byte[]? first = null;
        while (true)
        {
            TakeAvailable(sent, deadline, match, ref first, stopAtFirst: !drain);
            if (!drain && first is not null)
                return first;

            long left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0)
                return first;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = TimeSpan.FromSeconds((double)left / Stopwatch.Frequency);
            if (remaining > TimeSpan.FromMilliseconds(int.MaxValue))
                remaining = TimeSpan.FromMilliseconds(int.MaxValue);
            timeoutSource.CancelAfter(remaining);
            try
            {
                if (!await _inbound.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false))
                {
                    // The window can already hold a match. Ending the reader must not drop it.
                    if (first is not null)
                        return first;
                    ThrowIfDisposed();
                    throw ReaderEnded();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TakeAvailable(sent, deadline, match, ref first, stopAtFirst: !drain);
                if (!drain && first is not null)
                    return first;
                if (Stopwatch.GetTimestamp() >= deadline)
                    return first;
            }
            catch (CanOpenTransportException)
            {
                if (first is not null)
                    return first;
                ThrowIfDisposed();
                throw;
            }
        }
    }

    private static CanOpenTransportException ReaderEnded()
    {
        return new CanOpenTransportException("The LSS master reader ended before a slave answered.");
    }

    private void TakeAvailable(Sent sent, long deadline, Func<ReadOnlyMemory<byte>, bool> match, ref byte[]? first,
        bool stopAtFirst)
    {
        while (_inbound.Reader.TryRead(out var item))
        {
            if (!InWindow(sent, item.Arrival, deadline)) continue;
            if (!match(item.Data)) continue;
            first ??= item.Data.ToArray();
            if (stopAtFirst) return;
        }
    }

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
            await foreach (var frameEvent in _subscription.Frames.WithCancellation(_readerCts.Token)
                               .ConfigureAwait(false))
            {
                var data = frameEvent.Frame.Data;
                if (data.Length != LssFrames.Length) continue;
                // The event owns its payload for as long as the event lives. Only a frame that
                // matches a waiter is copied.
                _ = _inbound.Writer.TryWrite(new Inbound(frameEvent.HostArrivalTimestamp, data));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancels the reader. Completing this task is what lets Dispose finish.
        }
        catch (Exception ex)
        {
            _inbound.Writer.TryComplete(new CanOpenTransportException(
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
