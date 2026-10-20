using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Pdo;

/// <summary>The addressing mode of a multiplexed PDO (CiA 301 §7.2.3).</summary>
public enum MpdoAddressMode : byte
{
    /// <summary>The multiplexer names an object in the producer; mapping count FEh.</summary>
    Source = 0xFE,

    /// <summary>The multiplexer names an object in the consumer; mapping count FFh.</summary>
    Destination = 0xFF,
}

/// <summary>Additional PDO services on nodes created by this library.</summary>
public interface ICanOpenPdo : ICanOpenPdoMonitoring
{
    /// <summary>Configures an event-driven MPDO producer. At most one source-addressed
    /// producer is valid per node. MPDOs do not use RTR, SYNC or an event timer.</summary>
    void ConfigureMultiplexedTpdo(int pdoIndex, MpdoAddressMode mode, uint cobId);

    /// <summary>Configures an MPDO consumer. Source addressing uses the object dispatching
    /// lists (1FD0h–1FFFh); destination addressing writes the addressed local object.</summary>
    void ConfigureMultiplexedRpdo(int pdoIndex, MpdoAddressMode mode, uint cobId);

    /// <summary>Sends one source-addressed MPDO with the value of the named local object.
    /// The object must be readable, PDO-mappable and at most four bytes wide.</summary>
    Task SendSourceMpdoAsync(int pdoIndex, ushort index, byte subindex,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a destination-addressed MPDO. Node-id 0 addresses all DAM consumers.
    /// One to four data bytes are padded to the protocol's four-byte data field.</summary>
    Task SendDestinationMpdoAsync(int pdoIndex, byte destinationNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);
}
