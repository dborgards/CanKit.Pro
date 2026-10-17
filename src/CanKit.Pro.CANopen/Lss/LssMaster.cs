using System;
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
        var service = new CanBusService(bus);
        try
        {
            return new LssMaster(service, options ?? new LssMasterOptions(), ownsService: true);
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
        return new LssMaster(service, options ?? new LssMasterOptions(), ownsService: !leaveOpen);
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
    private readonly Channel<Inbound> _inbound = Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });

    private int _received;
    private int _disposed;

    internal LssMaster(ICanBusService service, LssMasterOptions options, bool ownsService)
    {
        _service = service;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _ownsService = ownsService;
        _subscription = service.Subscribe(frame =>
        {
            var can = frame.Frame;
            return !can.IsExtendedFrame && (uint)can.ID == CanOpenCobId.LssSlaveMessage;
        });
        _readerTask = Task.Run(ReadAsync);
    }

    /// <inheritdoc />
    public LssMasterOptions Options { get; }

    /// <inheritdoc />
    public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
    {
        if (state is not (LssState.Waiting or LssState.Configuration))
            throw new ArgumentOutOfRangeException(nameof(state), state, "LSS state is waiting or configuration.");
        return Execute(ct => SendAsync(LssFrames.SwitchGlobal(state), ct), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            int watermark = Watermark();
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveVendor, address.VendorId), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveProduct, address.ProductCode), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveRevision, address.RevisionNumber), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.SwitchSelectiveSerial, address.SerialNumber), ct)
                .ConfigureAwait(false);
            var response = await ReceiveAsync(watermark, static d => d[0] == LssCommand.SwitchSelectiveAcknowledge,
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
        CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            await SendAsync(LssFrames.ActivateBitTiming(switchDelayMilliseconds), ct).ConfigureAwait(false);
            var delay = TimeSpan.FromMilliseconds(switchDelayMilliseconds);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            if (bitsPerSecond is int rate && Options.BitRateSwitch is { } switchRate)
                await switchRate(rate, ct).ConfigureAwait(false);
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
            int watermark = Watermark();
            await SendAsync(LssFrames.Command(LssCommand.InquireNodeId), ct).ConfigureAwait(false);
            var response = await ReceiveRequiredAsync(watermark, LssCommand.InquireNodeId, ct).ConfigureAwait(false);
            return response[1];
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            int watermark = Watermark();
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteVendor, range.VendorId), ct).ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteProduct, range.ProductCode), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionLow, range.RevisionLow), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionHigh, range.RevisionHigh), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteSerialLow, range.SerialLow), ct)
                .ConfigureAwait(false);
            await SendAsync(LssFrames.WithU32(LssCommand.IdentifyRemoteSerialHigh, range.SerialHigh), ct)
                .ConfigureAwait(false);
            var response = await ReceiveAsync(watermark, static d => d[0] == LssCommand.IdentifySlave,
                Options.IdentificationTimeout, ct).ConfigureAwait(false);
            return response is not null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
        => Execute(async ct =>
        {
            int watermark = Watermark();
            await SendAsync(LssFrames.Command(LssCommand.IdentifyNonConfiguredRemote), ct).ConfigureAwait(false);
            var response = await ReceiveAsync(watermark, static d => d[0] == LssCommand.IdentifyNonConfiguredSlave,
                Options.IdentificationTimeout, ct).ConfigureAwait(false);
            return response is not null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken = default)
    {
        var payload = LssFrames.Manufacturer(commandSpecifier, parameters.Span);
        return Execute(async ct =>
        {
            int watermark = Watermark();
            await SendAsync(payload, ct).ConfigureAwait(false);
            return await ReceiveAsync(watermark, d => d[0] == commandSpecifier, Options.ResponseTimeout, ct)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _readerCts.Cancel();
        _subscription.Dispose();
        _inbound.Writer.TryComplete();
        try
        {
            _readerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The reader reports its own failure by ending. Disposal still finishes.
        }
        if (_ownsService) _service.Dispose();
        _gate.Dispose();
        _readerCts.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _readerCts.Cancel();
        _subscription.Dispose();
        _inbound.Writer.TryComplete();
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the reader stops because it was cancelled.
        }
        if (_ownsService) _service.Dispose();
        _gate.Dispose();
        _readerCts.Dispose();
    }

    private Task<LssServiceResult> ConfirmedAsync(byte[] request, byte commandSpecifier, CancellationToken cancellationToken)
        => Execute(async ct =>
        {
            int watermark = Watermark();
            await SendAsync(request, ct).ConfigureAwait(false);
            var response = await ReceiveRequiredAsync(watermark, commandSpecifier, ct).ConfigureAwait(false);
            return new LssServiceResult(response[1], response[2]);
        }, cancellationToken);

    private Task<uint> InquireU32Async(byte commandSpecifier, CancellationToken cancellationToken)
        => Execute(ct => InquireU32CoreAsync(commandSpecifier, ct), cancellationToken);

    private async Task<uint> InquireU32CoreAsync(byte commandSpecifier, CancellationToken cancellationToken)
    {
        int watermark = Watermark();
        await SendAsync(LssFrames.Command(commandSpecifier), cancellationToken).ConfigureAwait(false);
        var response = await ReceiveRequiredAsync(watermark, commandSpecifier, cancellationToken).ConfigureAwait(false);
        return LssFrames.ReadU32(response);
    }

    private async Task<T> Execute<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await body(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Dispose may have disposed the gate while this command was in flight.
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose passed the flag check and then disposed the gate.
                }
            }
        }
    }

    private Task Execute(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
        => Execute(async ct =>
        {
            await body(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private int Watermark() => Volatile.Read(ref _received);

    private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.LssMasterMessage), payload, isExtendedFrame: false);
        try
        {
            var confirmation = await _service.SendConfirmedAsync(frame, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!confirmation.Confirmed)
                throw new CanOpenTransportException(
                    $"LSS master frame 0x{payload[0]:X2} was not confirmed: {confirmation.FailureReason}.");
        }
        finally
        {
            frame.Dispose();
        }
    }

    private async Task<byte[]?> ReceiveAsync(int watermark, Func<byte[], bool> match, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (await _inbound.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                while (_inbound.Reader.TryRead(out var item))
                {
                    if (item.Sequence <= watermark) continue;
                    if (match(item.Data)) return item.Data;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        ThrowIfDisposed();
        return null;
    }

    private async Task<byte[]> ReceiveRequiredAsync(int watermark, byte commandSpecifier, CancellationToken cancellationToken)
    {
        var response = await ReceiveAsync(watermark, d => d[0] == commandSpecifier, Options.ResponseTimeout,
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
                var copy = data.ToArray();
                var sequence = Interlocked.Increment(ref _received);
                if (!_inbound.Writer.TryWrite(new Inbound(sequence, copy))) return;
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose.
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

    private readonly struct Inbound
    {
        public Inbound(int sequence, byte[] data)
        {
            Sequence = sequence;
            Data = data;
        }

        public int Sequence { get; }
        public byte[] Data { get; }
    }
}
