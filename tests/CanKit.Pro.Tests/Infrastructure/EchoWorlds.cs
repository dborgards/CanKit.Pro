using System;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using Xunit;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// Which of the two echo worlds a test is running in (#94).
/// </summary>
/// <remarks>
/// An adapter in <see cref="ChannelWorkMode.Echo"/> hands a sender its own frames back. Whether it
/// <em>says</em> so — <c>CanReceiveDataView.IsEcho</c> — is an adapter detail, and the two answers
/// produce different behaviour in every layer that has a self-traffic guard. Almost the whole
/// suite runs on CanKit.Adapter.Virtual, which echoes without flagging, so a test written there
/// exercises one world and says nothing about the other.
///
/// #94 records what that cost: two regressions in #93, both caught by review with the full suite
/// green, because each was a defect in the world the tests did not reach.
/// </remarks>
public enum EchoWorld
{
    /// <summary>
    /// Echoes and sets <c>IsEcho</c> — what SocketCAN, Kvaser and Vector do, modelled by
    /// <see cref="ControllableBus.EchoCapable"/>. Here the echo gate can actually gate.
    /// </summary>
    Flagging,

    /// <summary>
    /// Echoes without setting <c>IsEcho</c> — CanKit.Adapter.Virtual in
    /// <see cref="ChannelWorkMode.Echo"/>, i.e. the default test adapter. Here the echo gate is a
    /// no-op and only an identity check on the frame itself can tell self from peer.
    /// </summary>
    Unflagged,
}

/// <summary>
/// One bus that echoes — flagging or not per <see cref="EchoWorld"/> — plus a way to make a frame
/// arrive as if a <em>peer</em> had sent it. Lets a test assert both halves of a self-traffic
/// guard in either world, so "which world is this asserting?" is a parameter rather than a
/// property of whichever helper was reached for (#94, option 1).
/// </summary>
/// <remarks>
/// The two worlds need different machinery for the peer half, which is why this is a fixture and
/// not a factory method. <see cref="ControllableBus"/> is self-contained — it takes a real bus for
/// its configuration and never subscribes to it — so a peer there is a frame raised on the double
/// with <c>IsEcho == false</c>. The Virtual adapter is a real hub, so a peer there is a second bus
/// on the same session. Both produce the thing that matters: a frame the node under test did not
/// send.
/// </remarks>
public sealed class EchoWorldFixture : IDisposable
{
    private readonly ControllableBus? _flagging;
    private readonly ICanBus? _peerBus;

    private EchoWorldFixture(EchoWorld world, ICanBus bus, ControllableBus? flagging, ICanBus? peerBus)
    {
        World = world;
        Bus = bus;
        _flagging = flagging;
        _peerBus = peerBus;
    }

    /// <summary>Both worlds, for a <c>[Theory]</c> that must hold in each.</summary>
    public static TheoryData<EchoWorld> Both => new() { EchoWorld.Flagging, EchoWorld.Unflagged };

    public EchoWorld World { get; }

    /// <summary>The bus the component under test opens. Echoes its own transmissions.</summary>
    public ICanBus Bus { get; }

    public static EchoWorldFixture Create(EchoWorld world, string session)
    {
        switch (world)
        {
            case EchoWorld.Flagging:
                var controllable = ControllableBus.EchoCapable(session);
                return new EchoWorldFixture(world, controllable, controllable, peerBus: null);
            case EchoWorld.Unflagged:
                var own = VirtualAdapterFixture.Open(session, 0, ChannelWorkMode.Echo);
                var peer = VirtualAdapterFixture.Open(session, 1, ChannelWorkMode.Echo);
                return new EchoWorldFixture(world, own, flagging: null, peerBus: peer);
            default:
                throw new ArgumentOutOfRangeException(nameof(world), world, "Unknown echo world.");
        }
    }

    /// <summary>
    /// Makes <paramref name="frame"/> arrive at <see cref="Bus"/> as traffic from somebody else —
    /// never flagged as an echo in either world, because in neither world is it one.
    /// </summary>
    public void InjectPeerFrame(in CanFrame frame)
    {
        if (_flagging is not null) _flagging.RaiseObserved(frame, isEcho: false);
        else _peerBus!.Transmit(frame);
    }

    public void Dispose()
    {
        Bus.Dispose();
        _peerBus?.Dispose();
    }
}
