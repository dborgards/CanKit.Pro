namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Command specifiers of the CiA 305 LSS protocols (§7). Byte 0 of every LSS frame.
/// Specifiers 128..255 are reserved for the application (§7.3).
/// </summary>
public static class LssCommand
{
    /// <summary>Switch state global (§7.4.1). Mode follows in byte 1.</summary>
    public const byte SwitchStateGlobal = 0x04;

    /// <summary>Configure pending node-id (§7.5.1).</summary>
    public const byte ConfigureNodeId = 0x11;

    /// <summary>Configure pending bit timing (§7.5.2).</summary>
    public const byte ConfigureBitTiming = 0x13;

    /// <summary>Activate pending bit timing after the switch delay (§7.5.3). Unconfirmed.</summary>
    public const byte ActivateBitTiming = 0x15;

    /// <summary>Store pending node-id and bit timing (§7.5.4).</summary>
    public const byte StoreConfiguration = 0x17;

    /// <summary>Switch state selective, vendor-id (§7.4.2).</summary>
    public const byte SwitchSelectiveVendor = 0x40;

    /// <summary>Switch state selective, product code.</summary>
    public const byte SwitchSelectiveProduct = 0x41;

    /// <summary>Switch state selective, revision number.</summary>
    public const byte SwitchSelectiveRevision = 0x42;

    /// <summary>Switch state selective, serial number.</summary>
    public const byte SwitchSelectiveSerial = 0x43;

    /// <summary>Switch state selective confirmation, sent by the matching slave.</summary>
    public const byte SwitchSelectiveAcknowledge = 0x44;

    /// <summary>Identify remote slave, vendor-id (§7.7.1).</summary>
    public const byte IdentifyRemoteVendor = 0x46;

    /// <summary>Identify remote slave, product code.</summary>
    public const byte IdentifyRemoteProduct = 0x47;

    /// <summary>Identify remote slave, revision number low (inclusive).</summary>
    public const byte IdentifyRemoteRevisionLow = 0x48;

    /// <summary>Identify remote slave, revision number high (inclusive).</summary>
    public const byte IdentifyRemoteRevisionHigh = 0x49;

    /// <summary>Identify remote slave, serial number low (inclusive).</summary>
    public const byte IdentifyRemoteSerialLow = 0x4A;

    /// <summary>Identify remote slave, serial number high (inclusive).</summary>
    public const byte IdentifyRemoteSerialHigh = 0x4B;

    /// <summary>Identify non-configured remote slaves (§7.7.3).</summary>
    public const byte IdentifyNonConfiguredRemote = 0x4C;

    /// <summary>Identify slave (§7.7.2). No parameters; several slaves may send it.</summary>
    public const byte IdentifySlave = 0x4F;

    /// <summary>Identify non-configured slave (§7.7.4).</summary>
    public const byte IdentifyNonConfiguredSlave = 0x50;

    /// <summary>LSS Fastscan (§7.7.5).</summary>
    public const byte Fastscan = 0x51;

    /// <summary>Inquire vendor-id (§7.6.1.2).</summary>
    public const byte InquireVendorId = 0x5A;

    /// <summary>Inquire product code (§7.6.1.3).</summary>
    public const byte InquireProductCode = 0x5B;

    /// <summary>Inquire revision number (§7.6.1.4).</summary>
    public const byte InquireRevisionNumber = 0x5C;

    /// <summary>Inquire serial number (§7.6.1.5).</summary>
    public const byte InquireSerialNumber = 0x5D;

    /// <summary>Inquire node-id (§7.6.2).</summary>
    public const byte InquireNodeId = 0x5E;

    /// <summary>Switch state global mode: LSS waiting (§7.4.1).</summary>
    public const byte ModeWaiting = 0x00;

    /// <summary>Switch state global mode: LSS configuration (§7.4.1).</summary>
    public const byte ModeConfiguration = 0x01;

    /// <summary>
    /// Fastscan <c>BitCheck</c> that resets every unconfigured slave and makes them all respond
    /// (§6.7.2).
    /// </summary>
    public const byte FastscanReset = 0x80;

    /// <summary>Configure node-id succeeded (§7.5.1).</summary>
    public const byte NodeIdOk = 0x00;

    /// <summary>Configure node-id: node-id out of range.</summary>
    public const byte NodeIdOutOfRange = 0x01;

    /// <summary>Configure bit timing succeeded (§7.5.2).</summary>
    public const byte BitTimingOk = 0x00;

    /// <summary>Configure bit timing: the requested timing is not supported.</summary>
    public const byte BitTimingNotSupported = 0x01;

    /// <summary>Store configuration succeeded (§7.5.4).</summary>
    public const byte StoreOk = 0x00;

    /// <summary>Store configuration is not supported.</summary>
    public const byte StoreNotSupported = 0x01;

    /// <summary>Store configuration: the storage media could not be written.</summary>
    public const byte StoreMediaError = 0x02;

    /// <summary>
    /// Error code of a confirmed service when <c>spec-error</c> carries an implementation-specific
    /// code (§7.5). Any other non-zero code below this value is reserved by CiA.
    /// </summary>
    public const byte ManufacturerSpecific = 0xFF;

    /// <summary>Whether <paramref name="commandSpecifier"/> is in the application range 128..255.</summary>
    public static bool IsManufacturerSpecific(byte commandSpecifier) => commandSpecifier >= 128;
}
