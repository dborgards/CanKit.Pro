using System;
using System.Globalization;

namespace CanKit.Pro.J1939;

/// <summary>
/// What a decoded SPN field actually carries: a measurement, or one of the SAE J1939-71 §5.1.1
/// indicator ranges that occupy the top of every SPN's raw range.
/// </summary>
/// <remarks>
/// The numbering is deliberate: <see cref="NotAvailable"/> is 0 so that a default-initialised
/// <see cref="J1939SpnValue"/> reports "not available" rather than a valid measurement of zero.
/// </remarks>
public enum J1939SpnValueKind
{
    /// <summary>
    /// The transmitting ECU does not have the parameter, or was not asked for it. Unsigned: all
    /// raw bits set — <c>0xFF</c> for one byte, <c>0xFF00</c>..<c>0xFFFF</c> for two, and so on.
    /// <b>Signed SLOTs differ:</b> the code sits at the top of the <em>signed</em> range with the
    /// sign bit clear (<c>0x7F</c>, <c>0x7F00</c>..<c>0x7FFF</c>, …), and an all-bits-set field is
    /// then an ordinary <c>-1</c> measurement, not an indicator.
    /// </summary>
    NotAvailable = 0,

    /// <summary>A real measurement. <see cref="J1939SpnValue.Value"/> is meaningful.</summary>
    Valid = 1,

    /// <summary>
    /// The "parameter specific indicator" code (<c>0xFB</c> leading byte, or <c>0x7B</c> for a
    /// signed SLOT): its meaning is defined by the individual SPN, not by J1939-71, so it cannot
    /// be scaled into a physical value here.
    /// </summary>
    ParameterSpecific = 2,

    /// <summary>
    /// Reserved for future indicator bits (<c>0xFC</c>..<c>0xFD</c> leading byte, or
    /// <c>0x7C</c>..<c>0x7D</c> for a signed SLOT). Not a measurement.
    /// </summary>
    Reserved = 3,

    /// <summary>
    /// The transmitting ECU has the parameter but detected an error in it (<c>0xFE</c> leading
    /// byte, or <c>0x7E</c> for a signed SLOT).
    /// </summary>
    Error = 4,
}

/// <summary>
/// The result of decoding one SPN field: either a physical value, or the reason there is no
/// physical value to report (SAE J1939-71 §5.1.1, SRS FR-J1939-002).
/// </summary>
/// <remarks>
/// <para>
/// Every SPN reserves the top of its raw range for indicators. A 16-bit engine-speed field
/// reading <c>0xFFFF</c> means "not available", not 8191.875 rpm; <c>0xFE00</c>..<c>0xFEFF</c>
/// means the sending ECU knows its own reading is wrong. Returning a bare <see cref="double"/>
/// cannot distinguish those from a measurement, so extraction returns this type instead and
/// <see cref="Value"/> throws unless <see cref="IsValid"/> is true.
/// </para>
/// <para>
/// <see cref="Raw"/> is always the field's bit pattern as read off the wire, indicator or not, so
/// an application that wants to log or forward the exact code can still get at it.
/// </para>
/// </remarks>
public readonly record struct J1939SpnValue
{
    private readonly double _value;

    private J1939SpnValue(J1939SpnValueKind kind, ulong raw, double value)
    {
        Kind = kind;
        Raw = raw;
        _value = value;
    }

    /// <summary>What the field carries: a measurement or one of the indicator ranges.</summary>
    public J1939SpnValueKind Kind { get; }

    /// <summary>
    /// The raw field bits as read from the payload, right-aligned and zero-extended — for a
    /// signed SPN this is the two's-complement pattern, not the sign-extended number.
    /// </summary>
    public ulong Raw { get; }

    /// <summary>True when the field carries a real measurement.</summary>
    public bool IsValid => Kind == J1939SpnValueKind.Valid;

    /// <summary>
    /// True when the sending ECU does not have this parameter — all bits set for an unsigned
    /// SPN, or the sign-bit-clear <c>0x7F…</c> range for a signed one. See
    /// <see cref="J1939SpnValueKind.NotAvailable"/>.
    /// </summary>
    public bool IsNotAvailable => Kind == J1939SpnValueKind.NotAvailable;

    /// <summary>True when the sending ECU reported an error for this parameter.</summary>
    public bool IsError => Kind == J1939SpnValueKind.Error;

    /// <summary>
    /// The physical value, <c>raw × resolution + offset</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field carries an indicator rather than a
    /// measurement. Check <see cref="IsValid"/>, or use <see cref="TryGetValue"/> /
    /// <see cref="GetValueOrDefault"/>.</exception>
    public double Value => IsValid
        ? _value
        : throw new InvalidOperationException(
            $"SPN field carries the J1939-71 '{Kind}' indicator (raw 0x{Raw:X}), not a measurement.");

    /// <summary>
    /// Gets the physical value if the field carries one.
    /// </summary>
    /// <param name="value">The physical value, or 0 when this is an indicator.</param>
    /// <returns><see langword="true"/> if a measurement was returned.</returns>
    public bool TryGetValue(out double value)
    {
        value = IsValid ? _value : 0.0;
        return IsValid;
    }

    /// <summary>
    /// The physical value, or <paramref name="defaultValue"/> when the field carries an
    /// indicator. The default default is <see cref="double.NaN"/> so that an unguarded
    /// arithmetic use of an unavailable reading stays visibly wrong instead of looking plausible.
    /// </summary>
    public double GetValueOrDefault(double defaultValue = double.NaN)
        => IsValid ? _value : defaultValue;

    /// <summary>Creates a value carrying a real measurement.</summary>
    /// <param name="raw">The field's raw bit pattern.</param>
    /// <param name="value">The scaled physical value.</param>
    public static J1939SpnValue FromPhysical(ulong raw, double value)
        => new(J1939SpnValueKind.Valid, raw, value);

    /// <summary>Creates a value carrying one of the J1939-71 indicator ranges.</summary>
    /// <param name="kind">The indicator; must not be <see cref="J1939SpnValueKind.Valid"/>.</param>
    /// <param name="raw">The field's raw bit pattern.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is
    /// <see cref="J1939SpnValueKind.Valid"/> (use <see cref="FromPhysical"/>) or is not a defined
    /// <see cref="J1939SpnValueKind"/>.</exception>
    public static J1939SpnValue FromIndicator(J1939SpnValueKind kind, ulong raw)
    {
        if (kind == J1939SpnValueKind.Valid)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                $"{nameof(J1939SpnValueKind)}.{nameof(J1939SpnValueKind.Valid)} carries a measurement; use {nameof(FromPhysical)}.");
        }
        if (kind is < J1939SpnValueKind.NotAvailable or > J1939SpnValueKind.Error)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined SPN value kind.");
        }
        return new J1939SpnValue(kind, raw, 0.0);
    }

    /// <inheritdoc />
    public override string ToString() => IsValid
        ? _value.ToString("G", CultureInfo.InvariantCulture)
        : string.Format(CultureInfo.InvariantCulture, "{0} (raw 0x{1:X})", Kind, Raw);
}
