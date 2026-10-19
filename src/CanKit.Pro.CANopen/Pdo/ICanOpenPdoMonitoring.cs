using System;

namespace CanKit.Pro.CANopen.Pdo;

/// <summary>PDO deadline indications, without adding members to the published
/// <see cref="ICanOpenNode"/> interface.</summary>
public interface ICanOpenPdoMonitoring
{
    /// <summary>Raised once when an RPDO's event timer expires after a valid reception.
    /// A subsequent valid reception arms the timer again. Delivered on the node's event pump.</summary>
    event EventHandler<RpdoTimeoutEventArgs>? RpdoTimeout;
}

/// <summary>An RPDO deadline expired (CiA 301 §7.5.2.35, sub-index 05h).</summary>
public sealed class RpdoTimeoutEventArgs : EventArgs
{
    /// <summary>Constructs a deadline indication.</summary>
    public RpdoTimeoutEventArgs(int pdoIndex, uint cobId, TimeSpan timeout)
    {
        PdoIndex = pdoIndex;
        CobId = cobId;
        Timeout = timeout;
    }

    /// <summary>The RPDO number.</summary>
    public int PdoIndex { get; }

    /// <summary>The RPDO's CAN identifier.</summary>
    public uint CobId { get; }

    /// <summary>The configured event-timer interval.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>Access to PDO monitoring on nodes created by this library.</summary>
public static class CanOpenPdoExtensions
{
    /// <summary>Gets the node's PDO deadline indications.</summary>
    /// <exception cref="NotSupportedException">The node does not provide PDO monitoring.</exception>
    public static ICanOpenPdo Pdo(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node as ICanOpenPdo
            ?? throw new NotSupportedException("PDO monitoring is available on nodes CanOpen.OpenNode creates.");
    }
}
