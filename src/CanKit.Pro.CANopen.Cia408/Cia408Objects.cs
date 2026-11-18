using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The object catalogue of CiA 408 v2.2.0 clause 9: the one place in the library where an index
/// of the profile is written. The device side takes the indices, value types, access and
/// defaults of its object dictionary from here, the tool side those of the objects it reads and
/// writes, so neither can disagree with the other or with the norm.
/// </summary>
/// <remarks>
/// <para>The table itself is data, one line per object, in <c>Cia408Objects.Data.cs</c>,
/// grouped by the sub-clauses of the norm. It was made outside the repository by reconciling two
/// independent machine extractions of the norm — one from its text, one from the geometry of its
/// tables — with Annex A as a third opinion. 206 objects were checked by eye against
/// the printed page: every object the extractions disagreed on or one of them missed, every
/// object of §9.3, every setpoint and actual value the default PDOs map, every object under a
/// ruling below, and a random sample of 40 of the objects both extractions agree on, which showed
/// no error. The other 249 rest on the two extractions agreeing. Nothing of the norm is copied
/// into the repository but names, indices, types, defaults and paraphrased conditions.</para>
/// <para>What the tests pin: for every object the index, the object kind and the value type,
/// derived there independently of this table, and the reserved set; default, access, PDO access,
/// unit, prefix, category and condition only for the objects pinned in full (§9.3, the default-PDO
/// setpoints and actual values, the rulings, and a selection per area), plus rules every
/// definition has to satisfy.</para>
/// <para>Every index is that of logical device 0 (see
/// <see cref="Cia408ObjectDefinition.Index"/>). 455 objects are defined: 72 <c>VAR</c> and 383
/// value records (clause 9 has 456 object tables, because it defines <c>63D8h</c> twice; see
/// below). The 16 indices clause 9 marks reserved are listed in <see cref="Reserved"/> and have no
/// definition.</para>
/// <para>Rulings on the norm's own defects, each with both passages:</para>
/// <list type="bullet">
/// <item><description><c>63D8h</c> is defined twice: §9.12.10 "vprc control deviation 32-bit"
/// (Optional, ro, T, no default) and §9.14.19 "vprc control monitoring threshold 32-bit"
/// (Conditional on control monitoring type 2, rw, "TR, R or No", device specific). What decided:
/// Annex A of CiA 408 (Table 1027, p. 396) and CiA 408-C v1.0.0 Annex A Table A.2 (p. 32) both
/// list the index once, with the threshold's access (rw / TR). The catalogue holds §9.14.19; the
/// 32-bit control deviation of the pressure control has no index in this edition. CiA 408-B
/// v1.0.0 repeats both definitions (6.12.10, 6.15.7) and does not decide.</description></item>
/// <item><description>Two headings carry another index than their object table: §9.45.2
/// "Object 65D1h: dfpc control monitoring type" over a table that says <c>63D1h</c>, and §9.47.7
/// "Object 6605h: dpc T1" over a table that says <c>6604h</c>. Both <c>63D1h</c> and
/// <c>6604h</c> are defined elsewhere with other objects; the heading's index is taken.</description></item>
/// <item><description><c>6280h</c> is "Reserved" in §9.7.2, while Annex A (informative) lists it
/// as a RECORD "Controller output 32-bit": reserved.</description></item>
/// <item><description><c>6780h</c> and <c>6781h</c> are defined in §9.31, while Figure 4 calls
/// <c>6780h</c> to <c>67FEh</c> reserved and Annex A omits them: defined (see
/// <see cref="Cia408Area.ValveFlow"/>).</description></item>
/// </list>
/// <para>Where an entry description reads differently from its neighbours, the line in the data
/// file says what was read; the members of <see cref="Cia408ObjectDefinition"/> name the cases
/// that touch one of its fields.</para>
/// </remarks>
internal static partial class Cia408Objects
{
    // ---- the catalogue ----------------------------------------------------------------------------

    /// <summary>Every object of clause 9, ordered by index. Built by the static constructor in
    /// <c>Cia408Objects.Data.cs</c>.</summary>
    public static IReadOnlyList<Cia408ObjectDefinition> All { get; }

    /// <summary>The indices clause 9 marks "Reserved", ascending: <c>605Fh</c> (§9.4.9),
    /// <c>6280h</c> (§9.7.2), <c>63A4h</c>, <c>63C0h</c> to <c>63C6h</c> and <c>63E0h</c> to
    /// <c>63E2h</c> (§9.14), <c>6460h</c> to <c>6462h</c> (§9.18).</summary>
    public static IReadOnlyList<ushort> Reserved { get; }

    private static Dictionary<ushort, Cia408ObjectDefinition> ByIndex { get; }

    /// <summary>Looks an object up by its index in logical device 0.</summary>
    /// <returns><see langword="false"/> for an index clause 9 does not define, which includes
    /// the <see cref="Reserved"/> ones.</returns>
    public static bool TryGet(ushort indexInDevice0, out Cia408ObjectDefinition definition)
    {
        if (ByIndex.TryGetValue(indexInDevice0, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    // ---- device control, §9.3 ---------------------------------------------------------------------

    /// <summary>Object <c>6040h</c>: Device control word 16-bit (§9.3.2). The control word of a
    /// device with 16-bit parameter resolution (§8.6.2, dtprs); Mandatory, as is its 32-bit
    /// sibling <see cref="DeviceControlWord32"/>, although a device uses one resolution.</summary>
    public static Cia408ObjectDefinition DeviceControlWord16 { get; }

    /// <summary>Object <c>6041h</c>: Device status word 16-bit (§9.3.3).</summary>
    public static Cia408ObjectDefinition DeviceStatusWord16 { get; }

    /// <summary>Object <c>6042h</c>: Device mode (§9.3.4). "rw or ro" (ro if only one device mode
    /// is supported); the default 1 applies to the rw case, the ro case has none.</summary>
    public static Cia408ObjectDefinition DeviceMode { get; }

    /// <summary>Object <c>6043h</c>: Device control mode (§9.3.5). Optional, "rw or ro" (ro if only
    /// one control mode is supported), default manufacturer-specific.</summary>
    public static Cia408ObjectDefinition DeviceControlMode { get; }

    /// <summary>Object <c>6044h</c>: Device control word 32-bit (§9.3.6).</summary>
    public static Cia408ObjectDefinition DeviceControlWord32 { get; }

    /// <summary>Object <c>6045h</c>: Device status word 32-bit (§9.3.7).</summary>
    public static Cia408ObjectDefinition DeviceStatusWord32 { get; }

    /// <summary>Object <c>604Eh</c>: Device error code (§9.3.8).</summary>
    public static Cia408ObjectDefinition DeviceErrorCode { get; }

    /// <summary>Object <c>604Fh</c>: Device local (§9.3.9). Defaults to 0 (control word over the
    /// bus) in a valve or pump and to 1 (local) in a drive, so the catalogue holds
    /// <see cref="Cia408DefaultKind.DeviceKindSpecific"/>; "rw or ro" (ro if switching over the
    /// bus is not supported). The entry says "constant" although the object is writable.</summary>
    public static Cia408ObjectDefinition DeviceLocal { get; }

    // ---- setpoints and actual values of the default PDOs (§8.4 Table 13, §8.5 Table 32) -----------

    /// <summary>Object <c>6300h</c>: q setpoint (§9.8.2), RPDO mappings 2 and 4.</summary>
    public static Cia408ObjectDefinition QSetpoint { get; }

    /// <summary>Object <c>6301h</c>: Spool position actual value (§9.8.3), TPDO mappings 2 and 4.</summary>
    public static Cia408ObjectDefinition SpoolPositionActualValue { get; }

    /// <summary>Object <c>6308h</c>: q setpoint 32-bit (§9.8.5), RPDO mapping 2.</summary>
    public static Cia408ObjectDefinition QSetpoint32 { get; }

    /// <summary>Object <c>6309h</c>: Spool position actual value 32-bit (§9.8.6; the object
    /// description says "Spool actual value 32-bit"), TPDO mapping 2. Its unit is printed "ir",
    /// not "ir32" as for <see cref="QSetpoint32"/>.</summary>
    public static Cia408ObjectDefinition SpoolPositionActualValue32 { get; }

    /// <summary>Object <c>6380h</c>: vprc setpoint (§9.12.2), RPDO mappings 3, 4 and 10.</summary>
    public static Cia408ObjectDefinition VprcSetpoint { get; }

    /// <summary>Object <c>6381h</c>: vprc actual value (§9.12.3), TPDO mappings 3, 4 and 9.</summary>
    public static Cia408ObjectDefinition VprcActualValue { get; }

    /// <summary>Object <c>6388h</c>: vprc setpoint 32-bit (§9.12.5), RPDO mapping 3.</summary>
    public static Cia408ObjectDefinition VprcSetpoint32 { get; }

    /// <summary>Object <c>6389h</c>: vprc actual value 32-bit (§9.12.6), TPDO mapping 3. Printed
    /// with the default 0 although read-only, and with the unit "ir".</summary>
    public static Cia408ObjectDefinition VprcActualValue32 { get; }

    /// <summary>Object <c>6480h</c>: dcol setpoint (§9.32.2), RPDO mapping 5.</summary>
    public static Cia408ObjectDefinition DcolSetpoint { get; }

    /// <summary>Object <c>6500h</c>: dsc setpoint (§9.37.2), RPDO mapping 6.</summary>
    public static Cia408ObjectDefinition DscSetpoint { get; }

    /// <summary>Object <c>6501h</c>: dsc actual value (§9.37.3), TPDO mapping 5.</summary>
    public static Cia408ObjectDefinition DscActualValue { get; }

    /// <summary>Object <c>6580h</c>: dfpc setpoint (§9.42.2), RPDO mapping 7.</summary>
    public static Cia408ObjectDefinition DfpcSetpoint { get; }

    /// <summary>Object <c>6581h</c>: dfpc actual value (§9.42.3), TPDO mapping 6.</summary>
    public static Cia408ObjectDefinition DfpcActualValue { get; }

    /// <summary>Object <c>6600h</c>: dpc setpoint (§9.47.2), RPDO mapping 8.</summary>
    public static Cia408ObjectDefinition DpcSetpoint { get; }

    /// <summary>Object <c>6601h</c>: dpc actual value (§9.47.3), TPDO mapping 7.</summary>
    public static Cia408ObjectDefinition DpcActualValue { get; }

    /// <summary>Object <c>6700h</c>: vflc setpoint (§9.21.2), RPDO mappings 9 and 10.</summary>
    public static Cia408ObjectDefinition VflcSetpoint { get; }

    /// <summary>Object <c>6701h</c>: vflc actual value (§9.21.3), TPDO mappings 8 and 9.</summary>
    public static Cia408ObjectDefinition VflcActualValue { get; }

    /// <summary>Object <c>6708h</c>: vflc setpoint 32-bit (§9.21.5), RPDO mapping 9.</summary>
    public static Cia408ObjectDefinition VflcSetpoint32 { get; }

    /// <summary>Object <c>6709h</c>: vflc actual value 32-bit (§9.21.6), TPDO mapping 8.</summary>
    public static Cia408ObjectDefinition VflcActualValue32 { get; }

    // ---- builders ---------------------------------------------------------------------------------
    //
    // The data file writes each object the way its entry description reads: the access and PDO
    // codes, the category with its condition, and the default of the value (and of unit and
    // prefix for a value record) as one of the tokens below.

    /// <summary>The SDO access codes of the entry descriptions.</summary>
    private enum Acc
    {
        Ro,
        Rw,
        RwOrRo,
    }

    /// <summary>The PDO access codes of the entry descriptions, alternatives included.</summary>
    private enum Pdo
    {
        No,
        T,
        TOrNo,
        TrOrR,
        TrROrNo,
    }

    /// <summary>A category with the condition of a Conditional object.</summary>
    private readonly struct Cat
    {
        public Cat(Cia408Category category, string? condition)
        {
            Category = category;
            Condition = condition;
        }

        public Cia408Category Category { get; }

        public string? Condition { get; }
    }

    /// <summary>What a default value cell says: a kind and, for <see cref="Cia408DefaultKind.Value"/>,
    /// a number or a text.</summary>
    private readonly struct Dflt
    {
        public Dflt(Cia408DefaultKind kind, long? number = null, string? text = null)
        {
            Kind = kind;
            Number = number;
            Text = text;
        }

        public Cia408DefaultKind Kind { get; }

        public long? Number { get; }

        public string? Text { get; }
    }

    private static readonly Cat Mandatory = new(Cia408Category.Mandatory, null);
    private static readonly Cat Optional = new(Cia408Category.Optional, null);

    /// <summary>Conditional: mandatory if <paramref name="condition"/> (paraphrased).</summary>
    private static Cat If(string condition) => new(Cia408Category.Conditional, condition);

    /// <summary>"No".</summary>
    private static readonly Dflt No = new(Cia408DefaultKind.None);

    /// <summary>"Manufacturer-specific".</summary>
    private static readonly Dflt Ms = new(Cia408DefaultKind.ManufacturerSpecific);

    /// <summary>"Device specific".</summary>
    private static readonly Dflt Ds = new(Cia408DefaultKind.DeviceSpecific);

    /// <summary>"(control mode specific)".</summary>
    private static readonly Dflt Cms = new(Cia408DefaultKind.ControlModeSpecific);

    /// <summary>"See Table 12".</summary>
    private static readonly Dflt Ts = new(Cia408DefaultKind.TransducerSpecific);

    /// <summary>"ir (see sub-clause 7.3)".</summary>
    private static readonly Dflt Ir = new(Cia408DefaultKind.InternalResolution);

    /// <summary>"ir32 (see sub-clause 7.3)".</summary>
    private static readonly Dflt Ir32 = new(Cia408DefaultKind.InternalResolution32);

    /// <summary>0 in a valve or pump, 1 in a drive (<c>604Fh</c>).</summary>
    private static readonly Dflt ByDeviceKind = new(Cia408DefaultKind.DeviceKindSpecific);

    private static readonly Dflt Absent = new(Cia408DefaultKind.NotApplicable);

    /// <summary>A default value.</summary>
    private static Dflt V(long value) => new(Cia408DefaultKind.Value, value);

    /// <summary>A default text (of a string object).</summary>
    private static Dflt Text(string value) => new(Cia408DefaultKind.Value, text: value);

    /// <summary>A default unit, as its CiA 303-2 code.</summary>
    private static Dflt U(byte code) => new(Cia408DefaultKind.Value, code);

    /// <summary>A default prefix, as the power of ten.</summary>
    private static Dflt P(sbyte power) => new(Cia408DefaultKind.Value, power);

    /// <summary>A <c>VAR</c> at sub-index <c>00h</c>.</summary>
    private static Cia408ObjectDefinition Var(
        ushort index, string name, string clause, Cia408Area area, OdDataType type,
        Acc access, Pdo pdo, Cat category, Dflt value)
        => Make(index, name, clause, area, isValueRecord: false, type, access, pdo, category, value, Absent, Absent);

    /// <summary>A value record: value, unit and prefix at sub-indices <c>01h</c> to <c>03h</c>.</summary>
    private static Cia408ObjectDefinition Rec(
        ushort index, string name, string clause, Cia408Area area, OdDataType type,
        Acc access, Pdo pdo, Cat category, Dflt value, Dflt unit, Dflt prefix)
        => Make(index, name, clause, area, isValueRecord: true, type, access, pdo, category, value, unit, prefix);

    private static Cia408ObjectDefinition Make(
        ushort index, string name, string clause, Cia408Area area, bool isValueRecord, OdDataType type,
        Acc access, Pdo pdo, Cat category, Dflt value, Dflt unit, Dflt prefix)
    {
        if ((category.Category == Cia408Category.Conditional) != (category.Condition != null))
            throw new ArgumentException($"{index:X4}h: a condition belongs to a Conditional object and only there.", nameof(category));
        if (value.Kind is Cia408DefaultKind.InternalResolution or Cia408DefaultKind.InternalResolution32 or Cia408DefaultKind.NotApplicable
            || (value.Text != null && type != OdDataType.VisibleString))
            throw new ArgumentException($"{index:X4}h: not a default of a value.", nameof(value));
        if (isValueRecord && (unit.Kind is Cia408DefaultKind.NotApplicable or Cia408DefaultKind.None or Cia408DefaultKind.DeviceKindSpecific
            || prefix.Kind is Cia408DefaultKind.NotApplicable or Cia408DefaultKind.None or Cia408DefaultKind.DeviceKindSpecific
                or Cia408DefaultKind.InternalResolution or Cia408DefaultKind.InternalResolution32))
            throw new ArgumentException($"{index:X4}h: not a default of a unit or a prefix.", nameof(unit));

        var (pdoMost, pdoLeast) = pdo switch
        {
            Pdo.No => (Cia408PdoAccess.No, Cia408PdoAccess.No),
            Pdo.T => (Cia408PdoAccess.Transmit, Cia408PdoAccess.Transmit),
            Pdo.TOrNo => (Cia408PdoAccess.Transmit, Cia408PdoAccess.No),
            Pdo.TrOrR => (Cia408PdoAccess.Both, Cia408PdoAccess.Receive),
            Pdo.TrROrNo => (Cia408PdoAccess.Both, Cia408PdoAccess.No),
            _ => throw new ArgumentOutOfRangeException(nameof(pdo), pdo, null),
        };

        return new Cia408ObjectDefinition(
            index, name, clause, area, isValueRecord, type,
            access == Acc.Ro ? OdAccess.ReadOnly : OdAccess.ReadWrite,
            accessMayBeReadOnly: access == Acc.RwOrRo,
            pdoMost, pdoLeast,
            category.Category, category.Condition,
            value.Kind, value.Number, value.Text,
            unit.Kind, unit.Number is { } u ? checked((byte)u) : null,
            prefix.Kind, prefix.Number is { } p ? checked((sbyte)p) : null);
    }

    private static Dictionary<ushort, Cia408ObjectDefinition> Index(IEnumerable<Cia408ObjectDefinition> definitions)
    {
        var byIndex = new Dictionary<ushort, Cia408ObjectDefinition>();
        foreach (var d in definitions)
            byIndex.Add(d.Index, d); // throws on an index written twice
        return byIndex;
    }
}
