using System;
using System.Threading;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Listens for CANopen heartbeats and boot-up without taking a node-id or transmitting.
/// </summary>
/// <remarks>
/// <see cref="CanOpen.OpenNode(ICanBus, byte, CanOpenNodeOptions?)"/> announces itself with a
/// boot-up and answers NMT, SDO and node guarding. Discovery that only wants to hear who is
/// already on the bus (#131 decision 3) cannot use that. This observer subscribes to the NMT
/// error-control COB-IDs <c>0x701</c>–<c>0x77F</c> and raises
/// <see cref="HeartbeatReceived"/> for data frames. It never transmits.
/// Disposing it detaches the subscription; the bus itself stays open.
/// </remarks>
internal sealed class CanOpenHeartbeatObserver : IDisposable
{
    private readonly CanBusService _service;
    private readonly IDisposable _subscription;
    private int _disposed;

    private CanOpenHeartbeatObserver(CanBusService service)
    {
        _service = service;
        _subscription = service.Subscribe(OnFrame, IsHeartbeatFrame, includeEcho: false);
    }

    /// <summary>Raised for a heartbeat or boot-up. <see cref="NmtState.Initializing"/> is the
    /// boot-up frame (<c>data[0] == 0x00</c>).</summary>
    public event EventHandler<HeartbeatReceivedEventArgs>? HeartbeatReceived;

    /// <summary>Starts listening on <paramref name="bus"/>. Does not transmit.</summary>
    public static CanOpenHeartbeatObserver Open(ICanBus bus)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        return Attach(new CanBusService(bus));
    }

    /// <summary>
    /// Subscribes <paramref name="service"/>. If that fails, the service is disposed and the
    /// exception propagates, so a half-built observer does not leave the bus attached.
    /// </summary>
    internal static CanOpenHeartbeatObserver Attach(CanBusService service)
    {
        try
        {
            return new CanOpenHeartbeatObserver(service);
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscription.Dispose();
        _service.Dispose();
    }

    private static bool IsHeartbeatFrame(CanFrameEvent frameEvent)
    {
        var frame = frameEvent.Frame;
        if (frame.IsExtendedFrame || frame.IsRemoteFrame) return false;
        uint id = (uint)frame.ID;
        return id >= CanOpenCobId.HeartbeatBase + CanOpenCobId.MinNodeId
            && id <= CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId;
    }

    internal void OnFrame(CanFrameEvent frameEvent)
    {
        var frame = frameEvent.Frame;
        if (frame.Data.Length < 1) return;

        byte nodeId = (byte)((uint)frame.ID - CanOpenCobId.HeartbeatBase);
        // Same wire mapping as CanOpenNode.HandleHeartbeat: bit 7 is the guarding toggle,
        // which a heartbeat leaves clear.
        var state = ((byte)(frame.Data.Span[0] & 0x7F)) switch
        {
            0x00 => NmtState.Initializing,
            0x04 => NmtState.Stopped,
            0x05 => NmtState.Operational,
            0x7F => NmtState.PreOperational,
            _ => NmtState.Initializing,
        };

        HeartbeatReceived?.Invoke(
            this,
            new HeartbeatReceivedEventArgs(nodeId, state, DateTime.UtcNow));
    }
}
