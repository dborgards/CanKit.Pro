using System;

namespace CanKit.Pro.J1939;

/// <summary>
/// Static helpers for extracting SAE J1939-71 SPN (Suspect Parameter Number) values from a
/// PGN payload using configurable scale/offset (SRS FR-J1939-002).
/// </summary>
/// <remarks>
/// <para>
/// SAE J1939-71 encodes each SPN as an integer field of 1..64 bits — unsigned, or two's
/// complement for a signed SLOT — at a fixed byte (and bit) offset inside its PGN payload, with
/// a linear transform to physical units: <c>physical = raw * resolution + offset</c>.
/// </para>
/// <para>
/// Not every bit pattern is a measurement. §5.1.1 reserves the top of each SPN's raw range for
/// indicators — "not available", "error", reserved and parameter-specific codes — so
/// <see cref="Extract(ReadOnlySpan{byte}, int, int, int, double, double, bool)"/> returns a
/// <see cref="J1939SpnValue"/> that reports which of the two it found, rather than a bare
/// <see cref="double"/> that would turn a 16-bit <c>0xFFFF</c> ("not available") into a
/// plausible-looking 8191.875 rpm. <see cref="Classify"/> exposes the range check on its own.
/// </para>
/// <para>
/// Byte order is <b>little-endian</b>, matching SAE J1939-71 §5.1.3. Bit fields that start at
/// an offset within a byte are read across byte boundaries with the low bits coming from the
/// low-indexed byte, in line with the standard.
/// </para>
/// </remarks>
public static class J1939Spn
{
    /// <summary>
    /// Extracts an unsigned SPN raw value from <paramref name="payload"/> at
    /// <paramref name="byteOffset"/> starting at bit <paramref name="startBit"/> (0..7) and
    /// spanning <paramref name="bitLength"/> bits (1..64), little-endian.
    /// </summary>
    /// <param name="payload">The PGN payload bytes.</param>
    /// <param name="byteOffset">Zero-based byte position of the first bit.</param>
    /// <param name="startBit">Zero-based bit index within <paramref name="byteOffset"/> byte
    /// (0..7). 0 = least-significant bit of the byte, as in SAE J1939-71 §5.1.3.</param>
    /// <param name="bitLength">Number of bits (1..64).</param>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is out of range or the
    /// requested field extends past <paramref name="payload"/>.</exception>
    public static ulong ExtractRaw(ReadOnlySpan<byte> payload, int byteOffset, int startBit, int bitLength)
    {
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        if ((uint)startBit > 7) throw new ArgumentOutOfRangeException(nameof(startBit));
        if (bitLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(bitLength));
        int totalBits = startBit + bitLength;
        int bytesNeeded = byteOffset + ((totalBits + 7) >> 3);
        if (bytesNeeded > payload.Length)
            throw new ArgumentOutOfRangeException(nameof(bitLength),
                $"SPN field extends past the payload (offset={byteOffset}, startBit={startBit}, bitLength={bitLength}, payload={payload.Length}).");

        ulong acc = 0;
        int remaining = bitLength;
        int bitPos = startBit;
        int currentByte = byteOffset;
        int outBit = 0;
        while (remaining > 0)
        {
            int take = Math.Min(8 - bitPos, remaining);
            int mask = (1 << take) - 1;
            ulong chunk = (ulong)((payload[currentByte] >> bitPos) & mask);
            acc |= chunk << outBit;
            outBit += take;
            remaining -= take;
            currentByte++;
            bitPos = 0;
        }
        return acc;
    }

    /// <summary>
    /// Extracts an SPN raw value as a two's-complement signed integer, sign-extended from
    /// <paramref name="bitLength"/> bits to 64 (SAE J1939-71 signed SLOTs).
    /// </summary>
    /// <inheritdoc cref="ExtractRaw" path="/param"/>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is out of range or the
    /// requested field extends past <paramref name="payload"/>.</exception>
    public static long ExtractRawSigned(ReadOnlySpan<byte> payload, int byteOffset, int startBit, int bitLength)
        => SignExtend(ExtractRaw(payload, byteOffset, startBit, bitLength), bitLength);

    /// <summary>
    /// Classifies a raw SPN field against the SAE J1939-71 §5.1.1 indicator ranges, which occupy
    /// the top of every SPN's raw range and must not be scaled into physical values.
    /// </summary>
    /// <param name="raw">The field's raw bits, right-aligned and zero-extended (as returned by
    /// <see cref="ExtractRaw"/>) — for a signed SPN the two's-complement pattern, not the
    /// sign-extended number.</param>
    /// <param name="bitLength">Field size in bits (1..64).</param>
    /// <param name="isSigned">Whether the SPN is a two's-complement signed parameter.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitLength"/> is outside
    /// 1..64.</exception>
    /// <remarks>
    /// <para>
    /// J1939-71 defines the ranges on the field's <b>leading byte</b> for parameters of one byte
    /// or wider — unsigned <c>0xFB</c> parameter-specific, <c>0xFC</c>..<c>0xFD</c> reserved,
    /// <c>0xFE</c> error, <c>0xFF</c> not available — so a two-byte SPN is "not available" over
    /// <c>0xFF00</c>..<c>0xFFFF</c> and a four-byte one over <c>0xFF000000</c>..<c>0xFFFFFFFF</c>.
    /// For a signed parameter the same codes sit at the top of the signed range, i.e. with the
    /// sign bit clear: <c>0x7B</c>, <c>0x7C</c>..<c>0x7D</c>, <c>0x7E</c>, <c>0x7F</c>.
    /// </para>
    /// <para>
    /// Sub-byte parameters carry the codes as <b>individual values at the top of the field</b>
    /// ("terminal codes"), matching the 4-bit and 2-bit tables in §5.1.1: 4 bits reads
    /// <c>0xB</c> parameter-specific, <c>0xC</c>..<c>0xD</c> reserved, <c>0xE</c> error and
    /// <c>0xF</c> not available; 2 bits reads <c>0b10</c> error and <c>0b11</c> not available.
    /// The widths the standard does not tabulate keep that shape, scaled to the field top rather
    /// than to a leading group: 5..7 bits take all five codes (5 bits: 27 parameter-specific,
    /// 28..29 reserved, 30 error, 31 not available), and 3 bits takes the two of the 2-bit table
    /// (<c>0b110</c> error, <c>0b111</c> not available). Above a byte, widths other than
    /// 8/16/32 use the leading byte like the tabulated ones. A 1-bit field has no room for an
    /// indicator and is always <see cref="J1939SpnValueKind.Valid"/>, as is a signed field
    /// narrower than a byte — J1939-71 defines no indicator codes there, and inventing some would
    /// report real measurements as missing.
    /// </para>
    /// <para>
    /// The terminal reading for the untabulated sub-byte widths is taken from how real SPNs of
    /// those widths are defined, not from the tables (issue #99). SPN 527 "Cruise Control
    /// States" (CCVS1, PGN 65265) is a 3-bit field whose raw 4 and 5 are Resume and Set; the
    /// leading-group reading this replaced reported them as errors. SPN 527 also defines raw 6
    /// as a state (<c>AccelOverride</c>), which this reading reports as an error — a rule by
    /// width cannot know a parameter's own table, and a caller that has it should decode such a
    /// field from <see cref="ExtractRaw"/> instead. Every width above is pinned by known-answer
    /// tests so it cannot drift silently.
    /// </para>
    /// </remarks>
    public static J1939SpnValueKind Classify(ulong raw, int bitLength, bool isSigned = false)
    {
        if (bitLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(bitLength));

        // No indicator codes are defined for these, so every bit pattern is a measurement.
        if (bitLength == 1 || (isSigned && bitLength < 8)) return J1939SpnValueKind.Valid;

        // Below a byte the codes are single values at the top of the field; a 2- or 3-bit field
        // has room for the 2-bit table's two only.
        if (bitLength < 8) return Band(raw, (1UL << bitLength) - 1UL, allFiveCodes: bitLength >= 4);

        // A byte or wider: the codes are the top five values of the leading byte. For a signed
        // parameter the byte's top is the largest positive value, so the sign bit is clear.
        ulong lead = raw >> (bitLength - 8);
        return Band(lead, isSigned ? 0x7FUL : 0xFFUL, allFiveCodes: true);
    }

    private static J1939SpnValueKind Band(ulong value, ulong top, bool allFiveCodes)
    {
        if (value == top) return J1939SpnValueKind.NotAvailable;
        if (value == top - 1) return J1939SpnValueKind.Error;
        if (!allFiveCodes) return J1939SpnValueKind.Valid;
        if (value == top - 2 || value == top - 3) return J1939SpnValueKind.Reserved;
        if (value == top - 4) return J1939SpnValueKind.ParameterSpecific;
        return J1939SpnValueKind.Valid;
    }

    /// <summary>
    /// Applies the SAE J1939-71 §5.1.1 range check and, for a measurement, the linear transform
    /// <c>physical = raw * <paramref name="resolution"/> + <paramref name="offset"/></c>
    /// to an already-extracted raw field.
    /// </summary>
    /// <param name="raw">The field's raw bits, right-aligned and zero-extended.</param>
    /// <param name="bitLength">Field size in bits (1..64).</param>
    /// <param name="resolution">Physical units per raw increment.</param>
    /// <param name="offset">Physical value at raw 0.</param>
    /// <param name="isSigned">Whether the SPN is a two's-complement signed parameter; when true
    /// the raw bits are sign-extended before scaling.</param>
    public static J1939SpnValue FromRaw(ulong raw, int bitLength, double resolution, double offset,
        bool isSigned = false)
    {
        var kind = Classify(raw, bitLength, isSigned);
        if (kind != J1939SpnValueKind.Valid) return J1939SpnValue.FromIndicator(kind, raw);
        double scaled = isSigned
            ? SignExtend(raw, bitLength) * resolution + offset
            : raw * resolution + offset;
        return J1939SpnValue.FromPhysical(raw, scaled);
    }

    /// <summary>
    /// Extracts an SPN and applies the linear transform
    /// <c>physical = raw * <paramref name="resolution"/> + <paramref name="offset"/></c>
    /// (SRS FR-J1939-002, SAE J1939-71 §5.1.3).
    /// </summary>
    /// <param name="payload">The PGN payload bytes.</param>
    /// <param name="byteOffset">Zero-based byte position of the first bit.</param>
    /// <param name="startBit">Zero-based bit index within <paramref name="byteOffset"/> byte
    /// (0..7). 0 = least-significant bit of the byte, as in SAE J1939-71 §5.1.3.</param>
    /// <param name="bitLength">Number of bits (1..64).</param>
    /// <param name="resolution">Physical units per raw increment.</param>
    /// <param name="offset">Physical value at raw 0.</param>
    /// <param name="isSigned">Whether the SPN is a two's-complement signed parameter.</param>
    /// <returns>
    /// A <see cref="J1939SpnValue"/> that is either a measurement or one of the J1939-71 §5.1.1
    /// indicators. It is <b>not</b> a <see cref="double"/>: a field reading <c>0xFFFF</c> is
    /// "not available", and reporting it as 8191.875 rpm was the defect this return type exists
    /// to prevent.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is out of range or the
    /// requested field extends past <paramref name="payload"/>.</exception>
    public static J1939SpnValue Extract(ReadOnlySpan<byte> payload, int byteOffset, int startBit,
        int bitLength, double resolution, double offset, bool isSigned = false)
    {
        ulong raw = ExtractRaw(payload, byteOffset, startBit, bitLength);
        return FromRaw(raw, bitLength, resolution, offset, isSigned);
    }

    /// <summary>
    /// Overload for a byte-aligned SPN (starts at bit 0 of <paramref name="byteOffset"/>).
    /// </summary>
    /// <inheritdoc cref="Extract(ReadOnlySpan{byte}, int, int, int, double, double, bool)" path="/returns|/exception"/>
    public static J1939SpnValue Extract(ReadOnlySpan<byte> payload, int byteOffset, int bitLength,
        double resolution, double offset, bool isSigned = false)
        => Extract(payload, byteOffset, startBit: 0, bitLength, resolution, offset, isSigned);

    private static long SignExtend(ulong raw, int bitLength)
    {
        if (bitLength >= 64) return unchecked((long)raw);
        ulong signBit = 1UL << (bitLength - 1);
        return (raw & signBit) != 0
            ? unchecked((long)(raw | ~((1UL << bitLength) - 1UL)))
            : (long)raw;
    }

    /// <summary>
    /// Encodes an SPN raw value back into a payload buffer, little-endian. Useful for tests and
    /// for round-tripping simulated PGN payloads.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The requested field extends past
    /// <paramref name="payload"/>.</exception>
    public static void WriteRaw(Span<byte> payload, int byteOffset, int startBit, int bitLength, ulong rawValue)
    {
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        if ((uint)startBit > 7) throw new ArgumentOutOfRangeException(nameof(startBit));
        if (bitLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(bitLength));
        int totalBits = startBit + bitLength;
        int bytesNeeded = byteOffset + ((totalBits + 7) >> 3);
        if (bytesNeeded > payload.Length)
            throw new ArgumentOutOfRangeException(nameof(bitLength),
                $"SPN field extends past the payload (offset={byteOffset}, startBit={startBit}, bitLength={bitLength}, payload={payload.Length}).");

        int remaining = bitLength;
        int bitPos = startBit;
        int currentByte = byteOffset;
        int inBit = 0;
        while (remaining > 0)
        {
            int take = Math.Min(8 - bitPos, remaining);
            int mask = (1 << take) - 1;
            byte chunk = (byte)((rawValue >> inBit) & (ulong)mask);
            payload[currentByte] = (byte)((payload[currentByte] & ~(mask << bitPos)) | (chunk << bitPos));
            inBit += take;
            remaining -= take;
            currentByte++;
            bitPos = 0;
        }
    }
}
