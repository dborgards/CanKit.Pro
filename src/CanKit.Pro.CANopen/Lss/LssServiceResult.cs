using System;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Outcome of a confirmed LSS configuration service (configure node-id, configure bit timing,
/// store). A timeout is not an outcome of the service: it is <see cref="LssTimeoutException"/>
/// (CiA 305 annex A.2).
/// </summary>
public readonly struct LssServiceResult : IEquatable<LssServiceResult>
{
    /// <summary>Constructs a result. <paramref name="specError"/> is meaningful only when
    /// <paramref name="errorCode"/> is <see cref="LssCommand.ManufacturerSpecific"/>; otherwise
    /// the protocol reserves it and this value is what the slave sent.</summary>
    public LssServiceResult(byte errorCode, byte specError)
    {
        ErrorCode = errorCode;
        SpecError = specError;
    }

    /// <summary>Protocol succeeded.</summary>
    public static LssServiceResult Success => new(0, 0);

    /// <summary>Error code from the slave. <c>00h</c> is success. The meaning of the other codes
    /// depends on the service (CiA 305 §7.5).</summary>
    public byte ErrorCode { get; }

    /// <summary>
    /// Implementation-specific code. The slave sends it when <see cref="ErrorCode"/> is
    /// <see cref="LssCommand.ManufacturerSpecific"/>; otherwise the field is reserved.
    /// </summary>
    public byte SpecError { get; }

    /// <summary>The slave reported success (<see cref="ErrorCode"/> <c>00h</c>).</summary>
    public bool Succeeded => ErrorCode == 0;

    /// <summary>The slave reported <see cref="LssCommand.ManufacturerSpecific"/>.</summary>
    public bool IsManufacturerSpecific => ErrorCode == LssCommand.ManufacturerSpecific;

    /// <inheritdoc />
    public bool Equals(LssServiceResult other) => ErrorCode == other.ErrorCode && SpecError == other.SpecError;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LssServiceResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (ErrorCode << 8) | SpecError;

    /// <summary>Value equality.</summary>
    public static bool operator ==(LssServiceResult left, LssServiceResult right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(LssServiceResult left, LssServiceResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => Succeeded ? "success" : $"error 0x{ErrorCode:X2} spec 0x{SpecError:X2}";
}

/// <summary>How an LSS Fastscan ended (CiA 305 §6.7).</summary>
public enum LssFastscanStatus
{
    /// <summary>One unconfigured slave was identified and is now in LSS configuration.</summary>
    Found,

    /// <summary>The reset step (<c>BitCheck</c> <c>80h</c>) drew no response.</summary>
    NoUnconfiguredSlave,

    /// <summary>
    /// A bit pattern was proposed and no participating slave confirmed it. No slave was switched
    /// to configuration by the final step.
    /// </summary>
    NoMatch,
}

/// <summary>Result of <see cref="ILssMaster.FastscanAsync"/>.</summary>
public sealed class LssFastscanResult
{
    private LssFastscanResult(LssFastscanStatus status, LssAddress address)
    {
        Status = status;
        Address = address;
    }

    /// <summary>A slave was found and switched to configuration.</summary>
    public static LssFastscanResult Found(LssAddress address) => new(LssFastscanStatus.Found, address);

    /// <summary>No unconfigured slave answered the reset step.</summary>
    public static LssFastscanResult NoUnconfiguredSlave() => new(LssFastscanStatus.NoUnconfiguredSlave, default);

    /// <summary>The scan stopped because a proposed LSS number was not confirmed.</summary>
    public static LssFastscanResult NoMatch(LssAddress partial) => new(LssFastscanStatus.NoMatch, partial);

    /// <summary>How the scan ended.</summary>
    public LssFastscanStatus Status { get; }

    /// <summary>
    /// The address that was assembled. On <see cref="LssFastscanStatus.Found"/> it is the slave
    /// now in configuration. Parts that the request skipped are 0: they were not read.
    /// On <see cref="LssFastscanStatus.NoMatch"/> it is what had been determined before the miss.
    /// </summary>
    public LssAddress Address { get; }

    /// <summary><see cref="Status"/> is <see cref="LssFastscanStatus.Found"/>.</summary>
    public bool Succeeded => Status == LssFastscanStatus.Found;
}
