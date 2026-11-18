namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The function block of CiA 401-1 clause 8 an object belongs to (§8.2 to §8.7 and the
/// general device profile objects).</summary>
internal enum Cia401FunctionBlock
{
    /// <summary>Digital input module, §8.2: <c>6000h</c> to <c>6128h</c>.</summary>
    DigitalInput,

    /// <summary>Digital output module, §8.3: <c>6200h</c> to <c>6328h</c>.</summary>
    DigitalOutput,

    /// <summary>Analog input module and its set-ups, §8.4 and §8.6: <c>6400h</c> to <c>6404h</c>
    /// and <c>6421h</c> to <c>6432h</c>.</summary>
    AnalogInput,

    /// <summary>Analog output module and its set-ups, §8.5 and §8.7: <c>6410h</c> to
    /// <c>6414h</c>, <c>6441h</c> to <c>6447h</c> and <c>6450h</c>.</summary>
    AnalogOutput,

    /// <summary>The general device profile objects: <c>67FFh</c>.</summary>
    General,
}

/// <summary>The PDO mapping attribute of an entry description, as the norm gives it (CiA 301
/// §7.4.2): which PDO an entry can be mapped into, and whether the profile maps it by default.</summary>
internal enum Cia401PdoMapping
{
    /// <summary>"No": the entry cannot be mapped.</summary>
    No,

    /// <summary>"Optional": the entry can be mapped into a TPDO or an RPDO.</summary>
    Optional,

    /// <summary>"Default": the entry is mapped by the default PDOs of the profile (CiA 401-1 §6.2.5 to §6.2.12).</summary>
    Default,

    /// <summary>"TPDO": the entry can be mapped into a TPDO only.</summary>
    Tpdo,

    /// <summary>"RPDO": the entry can be mapped into an RPDO only.</summary>
    Rpdo,
}

/// <summary>
/// One object of CiA 401-1 v3.1.0 clause 8, in logical device 0: what the object and entry
/// descriptions say, once. The device side and the tool side take every index, data type, access
/// and default from here (<see cref="Cia401Objects"/>).
/// </summary>
/// <remarks>
/// The attributes are those of the elements, that is of sub-index <c>01h</c> and the ones that
/// follow it; sub-index <c>00h</c> of an array is always the const <c>UNSIGNED8</c> "highest
/// sub-index supported" (CiA 301 §7.4.5) and is not modelled here. Where the norm gives a
/// different value to a later element, the member says so.
/// </remarks>
internal sealed class Cia401ObjectDefinition
{
    internal Cia401ObjectDefinition(
        ushort index,
        string name,
        Cia401FunctionBlock block,
        bool isArray,
        OdDataType dataType,
        bool isManufacturerSpecific,
        OdAccess access,
        Cia401PdoMapping pdoMapping,
        long? defaultValue,
        int bitsPerElement,
        int firstChannel,
        byte maxSubindex,
        string elementNoun,
        string clause)
    {
        Index = index;
        Name = name;
        Block = block;
        IsArray = isArray;
        DataType = dataType;
        IsManufacturerSpecific = isManufacturerSpecific;
        Access = access;
        PdoMapping = pdoMapping;
        DefaultValue = defaultValue;
        BitsPerElement = bitsPerElement;
        FirstChannel = firstChannel;
        MaxSubindex = maxSubindex;
        ElementNoun = elementNoun;
        Clause = clause;
    }

    /// <summary>The object index in logical device 0 (shifted by <c>n·800h</c> for logical device
    /// <c>n</c>: each logical device has its own <c>800h</c> objects of the standardized profile
    /// area from <c>6000h</c>, CiA 301 v4.2.0 §7.4.1, Table 41).</summary>
    public ushort Index { get; }

    /// <summary>The object name as the heading of its clause gives it. The name of a range object
    /// (<c>6020h</c> to <c>6027h</c> and its siblings) is composed, not the clause heading, which
    /// names the whole family: the heading base followed by the channels of the index, for example
    /// "Read digital input bit 129 to 256" for <c>6021h</c>.</summary>
    public string Name { get; }

    /// <summary>The function block the object belongs to.</summary>
    public Cia401FunctionBlock Block { get; }

    /// <summary><see langword="true"/> for an <c>ARRAY</c> (or a manufacturer-specific
    /// <c>RECORD or ARRAY</c>), <see langword="false"/> for a <c>VAR</c> at sub-index
    /// <c>00h</c>.</summary>
    public bool IsArray { get; }

    /// <summary>The data type of the elements; <see cref="OdDataType.Domain"/> where the norm
    /// leaves it to the manufacturer (<see cref="IsManufacturerSpecific"/>).</summary>
    public OdDataType DataType { get; }

    /// <summary><see langword="true"/> where the norm gives the data type as
    /// "manufacturer-specific" (<c>6404h</c>, <c>6414h</c>).</summary>
    public bool IsManufacturerSpecific { get; }

    /// <summary>The access of the elements. The norm's <c>rw</c> allows <c>wo</c>, <c>ro</c> and
    /// <c>const</c> behaviour as well, its <c>ro</c> allows <c>const</c> (CiA 401-1 §8.1).</summary>
    public OdAccess Access { get; }

    /// <summary>The PDO mapping of sub-index <c>01h</c>. Where an entry description lists several
    /// values, the others are in the clause: <c>6200h</c> maps sub-indices <c>01h</c> to
    /// <c>08h</c> by default and the rest optionally, <c>6401h</c> and <c>6411h</c> do so for
    /// <c>01h</c> to <c>0Ch</c>, <c>6302h</c> and <c>6322h</c> say "No" at <c>01h</c> and
    /// "Optional" from <c>02h</c> on, and <c>6122h</c> says "No" at <c>01h</c> and <c>02h</c> and
    /// "Optional" at <c>FEh</c>.</summary>
    public Cia401PdoMapping PdoMapping { get; }

    /// <summary>The default value of the elements, or <see langword="null"/> where the norm says
    /// "No" (an input, a source) or "manufacturer-specific". BOOLEAN is 0 or 1; for the REAL32
    /// objects it is the numeric value of the real, which is integral wherever the norm gives
    /// one (<c>0,0</c> and <c>1,0</c>). 32-bit masks are given as the unsigned value.
    /// The value of a REAL32 object is not the bit pattern of the element: it must be converted
    /// (<c>BitConverter.SingleToInt32Bits((float)value)</c> or the like) before it is written to
    /// the element, and written raw <c>1</c> is the denormal <c>1.4E-45</c>, not <c>1.0</c>.</summary>
    public long? DefaultValue { get; }

    /// <summary>How many channels one sub-index carries: 1, 8, 16 or 32 for the digital objects,
    /// 0 for everything else (CiA 401-1 §7.2.1.3).</summary>
    public int BitsPerElement { get; }

    /// <summary>The channel carried by the first element: 1, except for the second to eighth
    /// object of a range of 128 channels per index (<c>6021h</c> starts at channel 129).</summary>
    public int FirstChannel { get; }

    /// <summary>The highest sub-index the norm allows: <c>FEh</c> for an array, <c>80h</c> for a
    /// range object, <c>08h</c> for the interrupt source banks, 0 for a variable.</summary>
    public byte MaxSubindex { get; }

    /// <summary>What one element of the object is called when it is described ("input",
    /// "output", "analog input", "analog output", "bank"); empty for a variable.</summary>
    public string ElementNoun { get; }

    /// <summary>The clause of CiA 401-1 v3.1.0 that defines the object, as the document numbers it
    /// (<c>8.2.1</c> for <c>6000h</c>). The general device profile objects are cited by their
    /// title: the document numbers that clause <c>8.3</c> a second time.</summary>
    public string Clause { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Index:X4}h {Name}";
}
