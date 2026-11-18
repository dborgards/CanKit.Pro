namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The part of CiA 408 v2.2.0 clause 9 an object is specified in. The areas follow the
/// sub-clauses of the norm and the index ranges of its Figure 4 (§9.2); where the two disagree,
/// the member says which one was followed.</summary>
internal enum Cia408Area
{
    /// <summary>Device control, §9.3: <c>6040h</c> to <c>604Fh</c>.</summary>
    DeviceControl,

    /// <summary>Device information, §9.4: <c>6050h</c> to <c>6057h</c>.</summary>
    DeviceInformation,

    /// <summary>Actual value conditioning for valves and pumps, §9.5: <c>6100h</c> to <c>617Fh</c>.</summary>
    ValveActualValueConditioning,

    /// <summary>Actual value conditioning for drives, §9.6: <c>6200h</c> to <c>627Fh</c>.</summary>
    DriveActualValueConditioning,

    /// <summary>Actuator conditioning 16-bit and 32-bit, §9.7: <c>6281h</c> to <c>62F0h</c> and
    /// the 16-bit objects <c>6324h</c>, <c>6340h</c> to <c>6347h</c> and <c>6360h</c> to
    /// <c>6362h</c>, which sit inside the index range of the spool position control but are
    /// specified in §9.7 (Figure 4 lists them separately).</summary>
    ActuatorConditioning,

    /// <summary>Spool position control (vpoc), §9.8 to §9.11: process values, setpoint
    /// conditioning, control monitoring and target window monitoring of control modes 1 and 2.</summary>
    ValveSpoolPosition,

    /// <summary>Pressure control (vprc), §9.12 to §9.15 (with the control monitoring the norm
    /// numbers §9.14.12 and on): control modes 3 and 4.</summary>
    ValvePressure,

    /// <summary>pQ control (vpqc), §9.16 to §9.20: control mode 5. §9.17 and §9.19 only refer to
    /// other clauses, §9.18 is reserved.</summary>
    ValvePower,

    /// <summary>Open loop movement control of drives (dcol), §9.32 to §9.36: control mode 6.</summary>
    DriveOpenLoop,

    /// <summary>Speed control of drives (dsc), §9.37 to §9.41: control mode 7.</summary>
    DriveVelocity,

    /// <summary>Pressure (force) control of drives (dfpc), §9.42 to §9.46: control mode 8.</summary>
    DrivePressure,

    /// <summary>Position control of drives (dpc), §9.47 to §9.51: control modes 9 and 10.</summary>
    DrivePosition,

    /// <summary>Flow control (vflc), §9.21 to §9.25, and the pFlow control with power limitation
    /// (vpflcpl) of §9.31: control modes 13 to 15. §9.31 is filed here because Figure 4 gives the
    /// flow range <c>6700h</c> to <c>677Fh</c> to control modes 13 to 15 and control mode 15 is
    /// the pFlow mode with power limitation; the brief's areas have no pFlow area of their own.
    /// Figure 4 calls the range of its two objects (<c>6780h</c> and on) reserved, while §9.31
    /// defines them: they are catalogued as defined.</summary>
    ValveFlow,
}

/// <summary>The category of an object as its object description gives it (CiA 301 v4.2.0
/// §7.4.6).</summary>
internal enum Cia408Category
{
    /// <summary>"Mandatory".</summary>
    Mandatory,

    /// <summary>"Optional".</summary>
    Optional,

    /// <summary>"Conditional": mandatory under the condition of the object's footnote
    /// (<see cref="Cia408ObjectDefinition.Condition"/>).</summary>
    Conditional,
}

/// <summary>
/// The PDO access of an entry, from the norm's codes <c>T</c>, <c>R</c>, <c>TR</c> and
/// <c>No</c>. CiA 408 v2.2.0 uses the codes in every entry description and in Annex A but never
/// defines them; the reading taken is the obvious one: <c>T</c> may be mapped into a TPDO,
/// <c>R</c> into an RPDO, <c>TR</c> into either, <c>No</c> into none. The values combine as bits
/// (<see cref="Both"/> is <see cref="Transmit"/> and <see cref="Receive"/>).
/// </summary>
internal enum Cia408PdoAccess
{
    /// <summary>"No": not mappable.</summary>
    No = 0,

    /// <summary>"T": mappable into a TPDO.</summary>
    Transmit = 1,

    /// <summary>"R": mappable into an RPDO.</summary>
    Receive = 2,

    /// <summary>"TR": mappable into a TPDO and into an RPDO.</summary>
    Both = Transmit | Receive,
}

/// <summary>What the default value column of an entry description says, for the value, the unit
/// or the prefix of an object.</summary>
internal enum Cia408DefaultKind
{
    /// <summary>A value is given; the definition carries it.</summary>
    Value,

    /// <summary>"No": the norm gives no default (an actual value, an input).</summary>
    None,

    /// <summary>"Manufacturer-specific".</summary>
    ManufacturerSpecific,

    /// <summary>"Device specific" — the norm's words for most thresholds of control and target
    /// window monitoring. Kept apart from <see cref="ManufacturerSpecific"/> because the norm keeps
    /// the two apart; neither is a value.</summary>
    DeviceSpecific,

    /// <summary>"(control mode specific)": the unit or prefix is that of the setpoint of the
    /// control mode in effect (Figure 4 key: internal resolution for the valve modes, mm/min,
    /// mbar, µm, ml/min for the drive and flow modes).</summary>
    ControlModeSpecific,

    /// <summary>"See Table 12": the unit or prefix depends on the transducer type
    /// (§8.3.1, Table 12).</summary>
    TransducerSpecific,

    /// <summary>"ir (see sub-clause 7.3)": the value is in the 16-bit internal resolution of
    /// Table 2 (±16384 = ±100 %), which has no CiA 303-2 unit code.</summary>
    InternalResolution,

    /// <summary>"ir32 (see sub-clause 7.3)": the 32-bit internal resolution of Table 3
    /// (±100000 = ±100 %).</summary>
    InternalResolution32,

    /// <summary>The default depends on the kind of device: object <c>604Fh</c> defaults to 0 in a
    /// valve or pump and to 1 in a drive (§9.3.9, footnotes b and c of its entry description).</summary>
    DeviceKindSpecific,

    /// <summary>The entry does not exist: a <c>VAR</c> has no unit and no prefix sub-index.</summary>
    NotApplicable,
}

/// <summary>
/// One object of CiA 408 v2.2.0 clause 9, in logical device 0: what its object description and
/// entry description say, once. The device side and the tool side take index, value type,
/// access, default, unit and prefix from here (<see cref="Cia408Objects"/>).
/// </summary>
/// <remarks>
/// <para>An object is a <c>VAR</c> at sub-index <c>00h</c> or a value record: a <c>RECORD</c> of
/// one of the complex data types <c>0080h</c> to <c>0087h</c> (§8.7.1), whose sub-index
/// <c>01h</c> is the value, <c>02h</c> the SI unit (UNSIGNED8) and <c>03h</c> the prefix
/// (INTEGER8), both coded after CiA 303-2. Access, PDO access and default of a value record are
/// those of sub-index <c>01h</c>, as Annex A footnote a states for its own columns.</para>
/// <para>The entries of a value record that are not modelled per object, because the norm gives
/// them the same way everywhere: sub-index <c>00h</c> (highest sub-index supported) is
/// Mandatory, ro, not mappable, with the value range <c>01h</c> to <c>03h</c> and a default that
/// is "Manufacturer-specific" in most tables and "No" in seven (<c>6254h</c>, <c>6281h</c>,
/// <c>6323h</c>, <c>6324h</c>, <c>6347h</c>, <c>670Ah</c>, <c>677Ah</c>); §8.7.1 on the other hand gives the record definitions
/// <c>0080h</c> to <c>0087h</c> the fixed value <c>03h</c>, and CiA 408-C v1.0.0 Annex A
/// Table A.1 has it ro. A device that implements <c>02h</c> and <c>03h</c> reports <c>03h</c>;
/// the catalogue does not choose between the passages. The one exception is <c>6347h</c>, whose
/// sub-index <c>00h</c> the norm gives as rw and variable (§9.7.34). Sub-indices <c>02h</c> and
/// <c>03h</c> are Optional, "rw or ro" and "variable or constant" in every table: writable where
/// unit and prefix can be configured over the bus, read-only otherwise (§8.3) — so a tool reads
/// them from the device rather than trusting <see cref="DefaultUnit"/> and
/// <see cref="DefaultPrefix"/>.</para>
/// <para>Where the object description and Annex A (informative) disagree — category, access
/// or value type of a handful of objects — the object description is taken, as Annex A itself
/// says to do (§A.1.1).</para>
/// </remarks>
internal sealed class Cia408ObjectDefinition
{
    internal Cia408ObjectDefinition(
        ushort index,
        string name,
        string clause,
        Cia408Area area,
        bool isValueRecord,
        OdDataType valueType,
        OdAccess access,
        bool accessMayBeReadOnly,
        Cia408PdoAccess pdoAccess,
        Cia408PdoAccess pdoAccessMinimum,
        Cia408Category category,
        string? condition,
        Cia408DefaultKind defaultValueKind,
        long? defaultValue,
        string? defaultText,
        Cia408DefaultKind defaultUnitKind,
        byte? defaultUnit,
        Cia408DefaultKind defaultPrefixKind,
        sbyte? defaultPrefix)
    {
        Index = index;
        Name = name;
        Clause = clause;
        Area = area;
        IsValueRecord = isValueRecord;
        ValueType = valueType;
        Access = access;
        AccessMayBeReadOnly = accessMayBeReadOnly;
        PdoAccess = pdoAccess;
        PdoAccessMinimum = pdoAccessMinimum;
        Category = category;
        Condition = condition;
        DefaultValueKind = defaultValueKind;
        DefaultValue = defaultValue;
        DefaultText = defaultText;
        DefaultUnitKind = defaultUnitKind;
        DefaultUnit = defaultUnit;
        DefaultPrefixKind = defaultPrefixKind;
        DefaultPrefix = defaultPrefix;
    }

    /// <summary>The object index in logical device 0. CiA 408 gives the ranges of the first
    /// logical device only (§9.2, Figure 4) and leaves further ones to CiA 301 (shifted by
    /// <c>n·800h</c>, CiA 301 v4.2.0 §7.4.1, Table 41), which the core package does in one
    /// helper.</summary>
    public ushort Index { get; }

    /// <summary>The object name as the object description of the norm gives it ("Object name"),
    /// which is shorter than the clause heading in places ("vlv actual value conditioning min
    /// pressure" for the heading's "… for pressure transducer"; "Device control word 16-bit" for
    /// the heading's "Device control word"). One exception: the object description of
    /// <c>6614h</c> says "dfpc setpoint conditioning hold setpoint" inside the dpc clause
    /// §9.47.16, whose heading says "dpc …"; the heading is taken.</summary>
    public string Name { get; }

    /// <summary>The clause of CiA 408 v2.2.0 that defines the object, as the document numbers it
    /// (<c>9.8.2</c> for <c>6300h</c>; the level-4 <c>9.10.7.1</c> for <c>6359h</c>; the
    /// objects of the pressure control monitoring, which the document numbers <c>9.14.14</c> to
    /// <c>9.14.21</c> under a sub-clause heading <c>9.14.12</c> inside §9.14).</summary>
    public string Clause { get; }

    /// <summary>The area of clause 9 the object belongs to.</summary>
    public Cia408Area Area { get; }

    /// <summary><see langword="true"/> for a value record (<c>RECORD</c> of one of <c>0080h</c>
    /// to <c>0087h</c>), <see langword="false"/> for a <c>VAR</c>.</summary>
    public bool IsValueRecord { get; }

    /// <summary>The data type of the <c>VAR</c>, or of sub-index <c>01h</c> of a value record as
    /// its record type defines it. Where the entry description's value range names another type
    /// (<c>6222h</c>: VALUE RECORD INTEGER32, value range INTEGER16, §9.6.17) the record type is
    /// taken. <c>VISIBLE_STRING64</c> (§9.4) is <see cref="OdDataType.VisibleString"/>; the norm
    /// names no length elsewhere.</summary>
    public OdDataType ValueType { get; }

    /// <summary>The SDO access of the <c>VAR</c> or of sub-index <c>01h</c>; where the norm says
    /// "rw or ro", the more permissive <see cref="OdAccess.ReadWrite"/>, with
    /// <see cref="AccessMayBeReadOnly"/> set.</summary>
    public OdAccess Access { get; }

    /// <summary><see langword="true"/> where the norm allows ro instead of rw: <c>6042h</c>
    /// (only one device mode supported), <c>6043h</c> (only one control mode supported) and
    /// <c>604Fh</c> (switching over the bus not supported), each by footnote a of its entry
    /// description.</summary>
    public bool AccessMayBeReadOnly { get; }

    /// <summary>The most permissive PDO access the norm allows for the <c>VAR</c> or sub-index
    /// <c>01h</c>. The entry descriptions also give alternatives — "TR, R or No", "TR or R",
    /// "T or No" — of which the implementer chooses one; the least permissive of them is
    /// <see cref="PdoAccessMinimum"/>, and the pair names each of the norm's codes uniquely
    /// ("TR, R or No" is <see cref="Cia408PdoAccess.Both"/> to <see cref="Cia408PdoAccess.No"/>).
    /// </summary>
    public Cia408PdoAccess PdoAccess { get; }

    /// <summary>The least permissive PDO access of the norm's alternatives; equal to
    /// <see cref="PdoAccess"/> where it gives only one code. What a tool can rely on: the control
    /// words <c>6040h</c> and <c>6044h</c> ("TR or R") are always RPDO-mappable, an object
    /// marked "TR, R or No" may not be mappable at all.</summary>
    public Cia408PdoAccess PdoAccessMinimum { get; }

    /// <summary>The category of the object description.</summary>
    public Cia408Category Category { get; }

    /// <summary>For a <see cref="Cia408Category.Conditional"/> object, the condition of its
    /// footnote ("Mandatory, if …"), paraphrased ("ramp type is 3"); <see langword="null"/>
    /// otherwise. Kept as the norm states it even where it does not fit: several drive objects
    /// (<c>6501h</c>, <c>6503h</c>, <c>6504h</c>, <c>6581h</c>, <c>6583h</c> to <c>6587h</c>,
    /// <c>6601h</c>, <c>6603h</c> to <c>6605h</c>) name "16-bit parameters" although drives are 32-bit only (§8.6.2, dtprs).
    /// </summary>
    public string? Condition { get; }

    /// <summary>What the norm says about the default of the <c>VAR</c> or sub-index <c>01h</c>.
    /// </summary>
    public Cia408DefaultKind DefaultValueKind { get; }

    /// <summary>The default value where <see cref="DefaultValueKind"/> is
    /// <see cref="Cia408DefaultKind.Value"/> and the object is numeric, else
    /// <see langword="null"/>. A hexadecimal default of a signed type is read in that type:
    /// <c>8000 0000h</c> of an INTEGER32 is −2147483648. <c>6042h</c> defaults to 1 if its
    /// access is rw and has no default if it is ro (§9.3.4); the catalogue holds the rw case,
    /// consistent with <see cref="Access"/>.</summary>
    public long? DefaultValue { get; }

    /// <summary>The default of a string object where the norm gives one: <c>"0"</c> for
    /// <c>6053h</c> (§9.4.4), the only VISIBLE_STRING64 object with a default other than
    /// "Manufacturer-specific"; <see langword="null"/> everywhere else.</summary>
    public string? DefaultText { get; }

    /// <summary>What the norm says about the default unit (sub-index <c>02h</c>); always
    /// <see cref="Cia408DefaultKind.NotApplicable"/> for a <c>VAR</c>.</summary>
    public Cia408DefaultKind DefaultUnitKind { get; }

    /// <summary>The default unit as a CiA 303-2 code (the device-profile codes <c>A0h</c> and on
    /// are those of §8.3.1, Table 11) where <see cref="DefaultUnitKind"/> is
    /// <see cref="Cia408DefaultKind.Value"/>; <c>0</c> is dimensionless. <c>6122h</c> gives
    /// "A7h (m², or 58h …)": <c>A7h</c> is held.</summary>
    public byte? DefaultUnit { get; }

    /// <summary>What the norm says about the default prefix (sub-index <c>03h</c>); always
    /// <see cref="Cia408DefaultKind.NotApplicable"/> for a <c>VAR</c>.</summary>
    public Cia408DefaultKind DefaultPrefixKind { get; }

    /// <summary>The default prefix, the power of ten of CiA 303-2 (<c>FDh</c> = −3, milli;
    /// <c>FAh</c> = −6, micro), where <see cref="DefaultPrefixKind"/> is
    /// <see cref="Cia408DefaultKind.Value"/>. The code is taken as printed where the label beside
    /// it disagrees: <c>62A3h</c>, <c>62B1h</c> to <c>62B3h</c>, <c>62C0h</c> and <c>62D1h</c>
    /// print "FAh, mili", and the code <c>FAh</c> (micro) is held although the label says milli —
    /// and although the 16-bit sibling of <c>62A3h</c>, <c>6347h</c>, has <c>FDh</c> with the same
    /// default and value range, which suggests milli was meant.</summary>
    public sbyte? DefaultPrefix { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Index:X4}h {Name}";
}
