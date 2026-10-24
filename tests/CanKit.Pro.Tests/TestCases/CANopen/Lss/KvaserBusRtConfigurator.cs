using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Abstractions.SPI.Common;
using CanKit.Core.Diagnostics;

// The namespace and name are those of the Kvaser adapter's runtime options in CanKit 0.5.6: the
// identity LssBitRateDetection recognises an adapter by. The test project does not reference the
// Kvaser adapter, so nothing collides with the real type.
namespace CanKit.Adapter.Kvaser;

/// <summary>
/// Stands in for the Kvaser adapter's runtime options (#345): a bus that reports
/// <see cref="ChannelWorkMode.ListenOnly"/> and advertises <see cref="CanFeature.ListenOnly"/>,
/// as CanKit 0.5.6 does for a channel with <c>canCHANNEL_CAP_SILENT_MODE</c>, while the adapter
/// never puts the controller into the silent output mode. Every value but the features is the
/// loopback configurator's, so the rate and the work mode are the ones the probe was opened with.
/// </summary>
internal sealed class KvaserBusRtConfigurator : IBusRTOptionsConfigurator
{
    private readonly IBusRTOptionsConfigurator _inner;

    public KvaserBusRtConfigurator(IBusRTOptionsConfigurator inner) => _inner = inner;

    public CanFeature Features => _inner.Features | CanFeature.ListenOnly;

    public Capability Capabilities => _inner.Capabilities;

    public int ChannelIndex => _inner.ChannelIndex;

    public string? ChannelName => _inner.ChannelName;

    public CanBusTiming BitTiming => _inner.BitTiming;

    public TxRetryPolicy TxRetryPolicy => _inner.TxRetryPolicy;

    public ChannelWorkMode WorkMode => _inner.WorkMode;

    public bool InternalResistance => _inner.InternalResistance;

    public CanProtocolMode ProtocolMode => _inner.ProtocolMode;

    public ICanFilter Filter => _inner.Filter;

    public CanFeature EnabledSoftwareFallback => _inner.EnabledSoftwareFallback;

    public bool AllowErrorInfo => _inner.AllowErrorInfo;

    public int AsyncBufferCapacity => _inner.AsyncBufferCapacity;

    public IBufferAllocator BufferAllocator => _inner.BufferAllocator;

    public CanExceptionPolicy? ExceptionPolicy
    {
        get => _inner.ExceptionPolicy;
        set => _inner.ExceptionPolicy = value;
    }
}
