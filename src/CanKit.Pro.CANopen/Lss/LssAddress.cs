using System;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// The 128-bit LSS address: vendor-id, product code, revision number and serial number, each
/// the corresponding sub-index of identity object <c>1018h</c> (CiA 305 §3.3, CiA 301).
/// </summary>
public readonly struct LssAddress : IEquatable<LssAddress>
{
    /// <summary>Constructs an address.</summary>
    public LssAddress(uint vendorId, uint productCode, uint revisionNumber, uint serialNumber)
    {
        VendorId = vendorId;
        ProductCode = productCode;
        RevisionNumber = revisionNumber;
        SerialNumber = serialNumber;
    }

    /// <summary>Identity <c>1018h:01</c>.</summary>
    public uint VendorId { get; }

    /// <summary>Identity <c>1018h:02</c>.</summary>
    public uint ProductCode { get; }

    /// <summary>Identity <c>1018h:03</c>.</summary>
    public uint RevisionNumber { get; }

    /// <summary>Identity <c>1018h:04</c>.</summary>
    public uint SerialNumber { get; }

    /// <summary>The four LSS numbers in the order Fastscan walks them (§6.7.2).</summary>
    public uint this[int index] => index switch
    {
        0 => VendorId,
        1 => ProductCode,
        2 => RevisionNumber,
        3 => SerialNumber,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "LSS number index is 0..3."),
    };

    /// <inheritdoc />
    public bool Equals(LssAddress other)
        => VendorId == other.VendorId
           && ProductCode == other.ProductCode
           && RevisionNumber == other.RevisionNumber
           && SerialNumber == other.SerialNumber;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LssAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)VendorId;
            hash = (hash * 397) ^ (int)ProductCode;
            hash = (hash * 397) ^ (int)RevisionNumber;
            hash = (hash * 397) ^ (int)SerialNumber;
            return hash;
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(LssAddress left, LssAddress right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(LssAddress left, LssAddress right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"0x{VendorId:X8}:0x{ProductCode:X8}:0x{RevisionNumber:X8}:0x{SerialNumber:X8}";
}

/// <summary>
/// Vendor-id, product code and inclusive ranges of revision and serial number, as the identify
/// remote slave service selects them (CiA 305 §6.6.2, §7.7.1).
/// </summary>
public readonly struct LssIdentityRange : IEquatable<LssIdentityRange>
{
    /// <summary>Constructs a range. Both bounds are included.</summary>
    /// <exception cref="ArgumentException">A low bound is above its high bound.</exception>
    public LssIdentityRange(uint vendorId, uint productCode, uint revisionLow, uint revisionHigh,
        uint serialLow, uint serialHigh)
    {
        if (revisionLow > revisionHigh)
            throw new ArgumentException("Revision low is above revision high.", nameof(revisionLow));
        if (serialLow > serialHigh)
            throw new ArgumentException("Serial low is above serial high.", nameof(serialLow));
        VendorId = vendorId;
        ProductCode = productCode;
        RevisionLow = revisionLow;
        RevisionHigh = revisionHigh;
        SerialLow = serialLow;
        SerialHigh = serialHigh;
    }

    /// <summary>A range that matches exactly <paramref name="address"/>.</summary>
    public LssIdentityRange(LssAddress address)
        : this(address.VendorId, address.ProductCode, address.RevisionNumber, address.RevisionNumber,
            address.SerialNumber, address.SerialNumber)
    {
    }

    /// <summary>Vendor-id that must match.</summary>
    public uint VendorId { get; }

    /// <summary>Product code that must match.</summary>
    public uint ProductCode { get; }

    /// <summary>Lowest revision number that matches, inclusive.</summary>
    public uint RevisionLow { get; }

    /// <summary>Highest revision number that matches, inclusive.</summary>
    public uint RevisionHigh { get; }

    /// <summary>Lowest serial number that matches, inclusive.</summary>
    public uint SerialLow { get; }

    /// <summary>Highest serial number that matches, inclusive.</summary>
    public uint SerialHigh { get; }

    /// <summary>Whether <paramref name="address"/> lies in this range, bounds included.</summary>
    public bool Contains(LssAddress address)
        => address.VendorId == VendorId
           && address.ProductCode == ProductCode
           && address.RevisionNumber >= RevisionLow && address.RevisionNumber <= RevisionHigh
           && address.SerialNumber >= SerialLow && address.SerialNumber <= SerialHigh;

    /// <inheritdoc />
    public bool Equals(LssIdentityRange other)
        => VendorId == other.VendorId && ProductCode == other.ProductCode
           && RevisionLow == other.RevisionLow && RevisionHigh == other.RevisionHigh
           && SerialLow == other.SerialLow && SerialHigh == other.SerialHigh;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LssIdentityRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)VendorId;
            hash = (hash * 397) ^ (int)ProductCode;
            hash = (hash * 397) ^ (int)RevisionLow;
            hash = (hash * 397) ^ (int)RevisionHigh;
            hash = (hash * 397) ^ (int)SerialLow;
            hash = (hash * 397) ^ (int)SerialHigh;
            return hash;
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(LssIdentityRange left, LssIdentityRange right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(LssIdentityRange left, LssIdentityRange right) => !left.Equals(right);
}
