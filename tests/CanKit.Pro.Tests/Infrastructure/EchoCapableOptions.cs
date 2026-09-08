using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Abstractions.SPI.Common;
using CanKit.Core.Diagnostics;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// A real bus configurator with two values overridden: <see cref="Features"/> always includes
/// <see cref="CanFeature.Echo"/>, and <see cref="WorkMode"/> is always
/// <see cref="ChannelWorkMode.Echo"/>.
///
/// <para>
/// <c>CanBusService.SendConfirmed</c> branches on exactly those two, so a test that wants the
/// real-echo path has to be able to state them. It cannot get them from an adapter: whether an
/// adapter declares the static <c>CanFeature.Echo</c> capability is its own business, and the
/// loopback adapter does not — which silently sends every echo test down the approximated path
/// instead of failing loudly. Everything else is delegated, so the double still reports a genuine
/// configurator's values rather than invented ones.
/// </para>
/// </summary>
internal sealed class EchoCapableOptions(IBusRTOptionsConfigurator inner) : IBusRTOptionsConfigurator
{
    public CanFeature Features => inner.Features | CanFeature.Echo;

    public ChannelWorkMode WorkMode => ChannelWorkMode.Echo;

    public Capability Capabilities => inner.Capabilities;

    public int ChannelIndex => inner.ChannelIndex;

    public string? ChannelName => inner.ChannelName;

    public CanBusTiming BitTiming => inner.BitTiming;

    public TxRetryPolicy TxRetryPolicy => inner.TxRetryPolicy;

    public bool InternalResistance => inner.InternalResistance;

    public CanProtocolMode ProtocolMode => inner.ProtocolMode;

    public ICanFilter Filter => inner.Filter;

    public CanFeature EnabledSoftwareFallback => inner.EnabledSoftwareFallback;

    public bool AllowErrorInfo => inner.AllowErrorInfo;

    public int AsyncBufferCapacity => inner.AsyncBufferCapacity;

    public IBufferAllocator BufferAllocator => inner.BufferAllocator;

    public CanExceptionPolicy? ExceptionPolicy
    {
        get => inner.ExceptionPolicy;
        set => inner.ExceptionPolicy = value;
    }
}
