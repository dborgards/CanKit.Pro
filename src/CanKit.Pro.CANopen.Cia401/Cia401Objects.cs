using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The object catalogue of CiA 401-1 v3.1.0 clause 8: the one place in the library where an index
/// of the profile is written. The device side takes the indices, data types, access and defaults
/// of its object dictionary from here, the tool side those of the objects it reads and writes, so
/// neither can disagree with the other or with the norm.
/// </summary>
/// <remarks>
/// <para>Every index is that of logical device 0; a logical device <c>n</c> is shifted by
/// <c>n·800h</c> (CiA 401-1 §6.2), which the core package does in one helper.</para>
/// <para><c>6420h</c> and <c>6440h</c> are "reserved for compatibility reason" (§8.6.1, §8.7.1)
/// and have no definition here. The norm's digital objects with a range of 128 channels per index
/// (<c>6020h</c> to <c>6027h</c> and their siblings) are one definition per index.</para>
/// <para>Where the entry description of the norm reads differently from the pattern of its
/// neighbours, or is missing, the member says what was read and how
/// (<see cref="DeviceType"/>).</para>
/// </remarks>
internal static class Cia401Objects
{
    private const byte HighestArraySubindex = 0xFE;
    private const byte HighestRangeSubindex = 0x80;

    // ---- Digital input module, CiA 401-1 §8.2 -----------------------------------------------------

    /// <summary>Object <c>6000h</c>: Read digital input 8-bit (CiA 401-1 §8.2.1).</summary>
    public static Cia401ObjectDefinition ReadDigitalInput8 { get; } = Digital(0x6000, "Read digital input 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadOnly, Cia401PdoMapping.Tpdo, null, "8.2.1");

    /// <summary>Object <c>6002h</c>: Polarity digital input 8-bit (CiA 401-1 §8.2.2).</summary>
    public static Cia401ObjectDefinition PolarityDigitalInput8 { get; } = Digital(0x6002, "Polarity digital input 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.2");

    /// <summary>Object <c>6003h</c>: Digital filter enable 8-bit (CiA 401-1 §8.2.3).</summary>
    public static Cia401ObjectDefinition DigitalFilterEnable8 { get; } = Digital(0x6003, "Digital filter enable 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.3");

    /// <summary>Object <c>6005h</c>: Global interrupt enable digital 8-bit (CiA 401-1 §8.2.4). A BOOLEAN variable, TRUE by default.</summary>
    public static Cia401ObjectDefinition GlobalInterruptEnableDigital8 { get; } = Variable(0x6005, "Global interrupt enable digital 8-bit", Cia401FunctionBlock.DigitalInput,
        Cia401PdoMapping.No, 1, "8.2.4");

    /// <summary>Object <c>6006h</c>: Digital interrupt mask any change 8-bit (CiA 401-1 §8.2.5).</summary>
    public static Cia401ObjectDefinition DigitalInterruptMaskAnyChange8 { get; } = Digital(0x6006, "Digital interrupt mask any change 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFF, "8.2.5");

    /// <summary>Object <c>6007h</c>: Digital interrupt mask low-to-high 8-bit (CiA 401-1 §8.2.6).</summary>
    public static Cia401ObjectDefinition DigitalInterruptMaskLowToHigh8 { get; } = Digital(0x6007, "Digital interrupt mask low-to-high 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.6");

    /// <summary>Object <c>6008h</c>: Digital interrupt mask high-to-low 8-bit (CiA 401-1 §8.2.7).</summary>
    public static Cia401ObjectDefinition DigitalInterruptMaskHighToLow8 { get; } = Digital(0x6008, "Digital interrupt mask high-to-low 8-bit", Cia401FunctionBlock.DigitalInput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.7");

    /// <summary>Objects <c>6020h</c> to <c>6027h</c>: read digital input bit, 128 channels per index (CiA 401-1 §8.2.8).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> ReadDigitalInputBit { get; } = Range(0x6020, "Read digital input bit", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.2.8");

    /// <summary>Objects <c>6030h</c> to <c>6037h</c>: polarity digital input bit, 128 channels per index (CiA 401-1 §8.2.9).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> PolarityDigitalInputBit { get; } = Range(0x6030, "Polarity digital input bit", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.9");

    /// <summary>Objects <c>6038h</c> to <c>603Fh</c>: filter constant digital input bit, 128 channels per index (CiA 401-1 §8.2.10).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> FilterConstantDigitalInputBit { get; } = Range(0x6038, "Filter constant digital input bit", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.10");

    /// <summary>Objects <c>6050h</c> to <c>6057h</c>: interrupt mask digital input bit any change, 128 channels per index (CiA 401-1 §8.2.11).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> InterruptMaskDigitalInputBitAnyChange { get; } = Range(0x6050, "Interrupt mask digital input bit any change", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.2.11");

    /// <summary>Objects <c>6060h</c> to <c>6067h</c>: interrupt mask digital input low-to-high bit, 128 channels per index (CiA 401-1 §8.2.12).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> InterruptMaskDigitalInputBitLowToHigh { get; } = Range(0x6060, "Interrupt mask digital input low-to-high bit", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.12");

    /// <summary>Objects <c>6070h</c> to <c>6077h</c>: interrupt mask digital input high-to-low bit, 128 channels per index (CiA 401-1 §8.2.13).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> InterruptMaskDigitalInputBitHighToLow { get; } = Range(0x6070, "Interrupt mask digital input high-to-low bit", Cia401FunctionBlock.DigitalInput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.13");

    /// <summary>Object <c>6100h</c>: Read digital input 16-bit (CiA 401-1 §8.2.14).</summary>
    public static Cia401ObjectDefinition ReadDigitalInput16 { get; } = Digital(0x6100, "Read digital input 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.2.14");

    /// <summary>Object <c>6102h</c>: Polarity digital input 16-bit (CiA 401-1 §8.2.15).</summary>
    public static Cia401ObjectDefinition PolarityDigitalInput16 { get; } = Digital(0x6102, "Polarity digital input 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.15");

    /// <summary>Object <c>6103h</c>: Digital filter enable 16-bit (CiA 401-1 §8.2.16).</summary>
    public static Cia401ObjectDefinition DigitalFilterEnable16 { get; } = Digital(0x6103, "Digital filter enable 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.16");

    /// <summary>Object <c>6106h</c>: Interrupt mask digital input any change 16-bit (CiA 401-1 §8.2.17).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputAnyChange16 { get; } = Digital(0x6106, "Interrupt mask digital input any change 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF, "8.2.17");

    /// <summary>Object <c>6107h</c>: Interrupt mask digital input low-to-high 16-bit (CiA 401-1 §8.2.18).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputLowToHigh16 { get; } = Digital(0x6107, "Interrupt mask digital input low-to-high 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.18");

    /// <summary>Object <c>6108h</c>: Interrupt mask digital input high-to-low 16-bit (CiA 401-1 §8.2.19).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputHighToLow16 { get; } = Digital(0x6108, "Interrupt mask digital input high-to-low 16-bit", Cia401FunctionBlock.DigitalInput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.19");

    /// <summary>Object <c>6120h</c>: Read digital input 32-bit (CiA 401-1 §8.2.20).</summary>
    public static Cia401ObjectDefinition ReadDigitalInput32 { get; } = Digital(0x6120, "Read digital input 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.2.20");

    /// <summary>Object <c>6122h</c>: Polarity digital input 32-bit (CiA 401-1 §8.2.21). The norm gives the PDO mapping "No" here, as against "Optional" for the 8-bit and 16-bit objects.</summary>
    public static Cia401ObjectDefinition PolarityDigitalInput32 { get; } = Digital(0x6122, "Polarity digital input 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.No, 0, "8.2.21");

    /// <summary>Object <c>6123h</c>: Digital filter enable 32-bit (CiA 401-1 §8.2.22).</summary>
    public static Cia401ObjectDefinition DigitalFilterEnable32 { get; } = Digital(0x6123, "Digital filter enable 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.22");

    /// <summary>Object <c>6126h</c>: Interrupt mask digital input any change 32-bit (CiA 401-1 §8.2.23).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputAnyChange32 { get; } = Digital(0x6126, "Interrupt mask digital input any change 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF_FFFFL, "8.2.23");

    /// <summary>Object <c>6127h</c>: Interrupt mask digital input low-to-high 32-bit (CiA 401-1 §8.2.24).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputLowToHigh32 { get; } = Digital(0x6127, "Interrupt mask digital input low-to-high 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.24");

    /// <summary>Object <c>6128h</c>: Interrupt mask digital input high-to-low 32-bit (CiA 401-1 §8.2.25).</summary>
    public static Cia401ObjectDefinition InterruptMaskDigitalInputHighToLow32 { get; } = Digital(0x6128, "Interrupt mask digital input high-to-low 32-bit", Cia401FunctionBlock.DigitalInput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.2.25");

    // ---- Digital output module, CiA 401-1 §8.3 ----------------------------------------------------

    /// <summary>Object <c>6200h</c>: Write digital output 8-bit (CiA 401-1 §8.3.1).</summary>
    public static Cia401ObjectDefinition WriteDigitalOutput8 { get; } = Digital(0x6200, "Write digital output 8-bit", Cia401FunctionBlock.DigitalOutput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Default, 0, "8.3.1");

    /// <summary>Object <c>6202h</c>: Change polarity digital output 8-bit (CiA 401-1 §8.3.2).</summary>
    public static Cia401ObjectDefinition ChangePolarityDigitalOutput8 { get; } = Digital(0x6202, "Change polarity digital output 8-bit", Cia401FunctionBlock.DigitalOutput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.2");

    /// <summary>Object <c>6206h</c>: Error mode digital output 8-bit (CiA 401-1 §8.3.3).</summary>
    public static Cia401ObjectDefinition ErrorModeDigitalOutput8 { get; } = Digital(0x6206, "Error mode digital output 8-bit", Cia401FunctionBlock.DigitalOutput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFF, "8.3.3");

    /// <summary>Object <c>6207h</c>: Error value digital output 8-bit (CiA 401-1 §8.3.4).</summary>
    public static Cia401ObjectDefinition ErrorValueDigitalOutput8 { get; } = Digital(0x6207, "Error value digital output 8-bit", Cia401FunctionBlock.DigitalOutput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.4");

    /// <summary>Object <c>6208h</c>: Filter mask digital output 8-bit (CiA 401-1 §8.3.5).</summary>
    public static Cia401ObjectDefinition FilterMaskDigitalOutput8 { get; } = Digital(0x6208, "Filter mask digital output 8-bit", Cia401FunctionBlock.DigitalOutput, 8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFF, "8.3.5");

    /// <summary>Objects <c>6220h</c> to <c>6227h</c>: write digital output bit, 128 channels per index (CiA 401-1 §8.3.6).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> WriteDigitalOutputBit { get; } = Range(0x6220, "Write digital output bit", Cia401FunctionBlock.DigitalOutput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.6");

    /// <summary>Objects <c>6240h</c> to <c>6247h</c>: change polarity digital output bit, 128 channels per index (CiA 401-1 §8.3.7).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> ChangePolarityDigitalOutputBit { get; } = Range(0x6240, "Change polarity digital output bit", Cia401FunctionBlock.DigitalOutput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.7");

    /// <summary>Objects <c>6250h</c> to <c>6257h</c>: error mode digital output lines, 128 channels per index (CiA 401-1 §8.3.8).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> ErrorModeDigitalOutputBit { get; } = Range(0x6250, "Error mode digital output lines", Cia401FunctionBlock.DigitalOutput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.3.8");

    /// <summary>Objects <c>6260h</c> to <c>6267h</c>: error value digital output bit, 128 channels per index (CiA 401-1 §8.3.9).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> ErrorValueDigitalOutputBit { get; } = Range(0x6260, "Error value digital output bit", Cia401FunctionBlock.DigitalOutput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.9");

    /// <summary>Objects <c>6270h</c> to <c>6277h</c>: filter mask digital output bit, 128 channels per index (CiA 401-1 §8.3.10).</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> FilterMaskDigitalOutputBit { get; } = Range(0x6270, "Filter mask digital output bit", Cia401FunctionBlock.DigitalOutput,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.3.10");

    /// <summary>Object <c>6300h</c>: Write digital output 16-bit (CiA 401-1 §8.3.11).</summary>
    public static Cia401ObjectDefinition WriteDigitalOutput16 { get; } = Digital(0x6300, "Write digital output 16-bit", Cia401FunctionBlock.DigitalOutput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.11");

    /// <summary>Object <c>6302h</c>: Change polarity digital output 16-bit (CiA 401-1 §8.3.12).</summary>
    public static Cia401ObjectDefinition ChangePolarityDigitalOutput16 { get; } = Digital(0x6302, "Change polarity digital output 16-bit", Cia401FunctionBlock.DigitalOutput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.No, 0, "8.3.12");

    /// <summary>Object <c>6306h</c>: Error mode digital output 16-bit (CiA 401-1 §8.3.13).</summary>
    public static Cia401ObjectDefinition ErrorModeDigitalOutput16 { get; } = Digital(0x6306, "Error mode digital output 16-bit", Cia401FunctionBlock.DigitalOutput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF, "8.3.13");

    /// <summary>Object <c>6307h</c>: Error value digital output 16-bit (CiA 401-1 §8.3.14).</summary>
    public static Cia401ObjectDefinition ErrorValueDigitalOutput16 { get; } = Digital(0x6307, "Error value digital output 16-bit", Cia401FunctionBlock.DigitalOutput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.14");

    /// <summary>Object <c>6308h</c>: Filter mask digital output 16-bit (CiA 401-1 §8.3.15).</summary>
    public static Cia401ObjectDefinition FilterMaskDigitalOutput16 { get; } = Digital(0x6308, "Filter mask digital output 16-bit", Cia401FunctionBlock.DigitalOutput, 16,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF, "8.3.15");

    /// <summary>Object <c>6320h</c>: Write digital output 32-bit (CiA 401-1 §8.3.16).</summary>
    public static Cia401ObjectDefinition WriteDigitalOutput32 { get; } = Digital(0x6320, "Write digital output 32-bit", Cia401FunctionBlock.DigitalOutput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.16");

    /// <summary>Object <c>6322h</c>: Change polarity digital output 32-bit (CiA 401-1 §8.3.17).</summary>
    public static Cia401ObjectDefinition ChangePolarityDigitalOutput32 { get; } = Digital(0x6322, "Change polarity digital output 32-bit", Cia401FunctionBlock.DigitalOutput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.No, 0, "8.3.17");

    /// <summary>Object <c>6326h</c>: Error mode digital output 32-bit (CiA 401-1 §8.3.18).</summary>
    public static Cia401ObjectDefinition ErrorModeDigitalOutput32 { get; } = Digital(0x6326, "Error mode digital output 32-bit", Cia401FunctionBlock.DigitalOutput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF_FFFFL, "8.3.18");

    /// <summary>Object <c>6327h</c>: Error value digital output 32-bit (CiA 401-1 §8.3.19).</summary>
    public static Cia401ObjectDefinition ErrorValueDigitalOutput32 { get; } = Digital(0x6327, "Error value digital output 32-bit", Cia401FunctionBlock.DigitalOutput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.3.19");

    /// <summary>Object <c>6328h</c>: Filter mask digital output 32-bit (CiA 401-1 §8.3.20).</summary>
    public static Cia401ObjectDefinition FilterMaskDigitalOutput32 { get; } = Digital(0x6328, "Filter mask digital output 32-bit", Cia401FunctionBlock.DigitalOutput, 32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFFFF_FFFFL, "8.3.20");

    // ---- Analog input module, CiA 401-1 §8.4 ------------------------------------------------------

    /// <summary>Object <c>6400h</c>: Read analog input 8-bit (CiA 401-1 §8.4.1).</summary>
    public static Cia401ObjectDefinition ReadAnalogInput8 { get; } = Analog(0x6400, "Read analog input 8-bit", Cia401FunctionBlock.AnalogInput, OdDataType.Integer8,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.4.1");

    /// <summary>Object <c>6401h</c>: Read analog input 16-bit (CiA 401-1 §8.4.2).</summary>
    public static Cia401ObjectDefinition ReadAnalogInput16 { get; } = Analog(0x6401, "Read analog input 16-bit", Cia401FunctionBlock.AnalogInput, OdDataType.Integer16,
        OdAccess.ReadOnly, Cia401PdoMapping.Default, null, "8.4.2");

    /// <summary>Object <c>6402h</c>: Read analog input 32-bit (CiA 401-1 §8.4.3).</summary>
    public static Cia401ObjectDefinition ReadAnalogInput32 { get; } = Analog(0x6402, "Read analog input 32-bit", Cia401FunctionBlock.AnalogInput, OdDataType.Integer32,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.4.3");

    /// <summary>Object <c>6403h</c>: Read analog input float (CiA 401-1 §8.4.4).</summary>
    public static Cia401ObjectDefinition ReadAnalogInputFloat { get; } = Analog(0x6403, "Read analog input float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.4.4");

    /// <summary>Object <c>6404h</c>: Read manufacturer-specific analog input (CiA 401-1 §8.4.5).</summary>
    public static Cia401ObjectDefinition ReadManufacturerSpecificAnalogInput { get; } = Analog(0x6404, "Read manufacturer-specific analog input", Cia401FunctionBlock.AnalogInput, OdDataType.Domain,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.4.5", manufacturerSpecific: true);

    // ---- Analog output module, CiA 401-1 §8.5 -----------------------------------------------------

    /// <summary>Object <c>6410h</c>: Write analog output 8-bit (CiA 401-1 §8.5.1).</summary>
    public static Cia401ObjectDefinition WriteAnalogOutput8 { get; } = Analog(0x6410, "Write analog output 8-bit", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.5.1");

    /// <summary>Object <c>6411h</c>: Write analog output 16-bit (CiA 401-1 §8.5.2).</summary>
    public static Cia401ObjectDefinition WriteAnalogOutput16 { get; } = Analog(0x6411, "Write analog output 16-bit", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer16,
        OdAccess.ReadWrite, Cia401PdoMapping.Default, 0, "8.5.2");

    /// <summary>Object <c>6412h</c>: Write analog output 32-bit (CiA 401-1 §8.5.3).</summary>
    public static Cia401ObjectDefinition WriteAnalogOutput32 { get; } = Analog(0x6412, "Write analog output 32-bit", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.5.3");

    /// <summary>Object <c>6413h</c>: Write analog output float (CiA 401-1 §8.5.4).</summary>
    public static Cia401ObjectDefinition WriteAnalogOutputFloat { get; } = Analog(0x6413, "Write analog output float", Cia401FunctionBlock.AnalogOutput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.5.4");

    /// <summary>Object <c>6414h</c>: Write manufacturer-specific analog output (CiA 401-1 §8.5.5).</summary>
    public static Cia401ObjectDefinition WriteManufacturerSpecificAnalogOutput { get; } = Analog(0x6414, "Write manufacturer-specific analog output", Cia401FunctionBlock.AnalogOutput, OdDataType.Domain,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, null, "8.5.5", manufacturerSpecific: true);

    // ---- Analog input set-ups, CiA 401-1 §8.6 (6420h is reserved, §8.6.1) -------------------------

    /// <summary>Object <c>6421h</c>: Analog input interrupt trigger selection (CiA 401-1 §8.6.2).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptTriggerSelection { get; } = Analog(0x6421, "Analog input interrupt trigger selection", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0x07, "8.6.2");

    /// <summary>Object <c>6422h</c>: Analog input interrupt source (CiA 401-1 §8.6.3). One element is an interrupt source bank, at most eight.</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptSource { get; } = Analog(0x6422, "Analog input interrupt source", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned32,
        OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, "8.6.3", noun: "bank", maxSubindex: 8);

    /// <summary>Object <c>6423h</c>: Analog input global interrupt enable (CiA 401-1 §8.6.4). A BOOLEAN variable, FALSE by default.</summary>
    public static Cia401ObjectDefinition AnalogInputGlobalInterruptEnable { get; } = Variable(0x6423, "Analog input global interrupt enable", Cia401FunctionBlock.AnalogInput,
        Cia401PdoMapping.Optional, 0, "8.6.4");

    /// <summary>Object <c>6424h</c>: Analog input interrupt upper limit integer (CiA 401-1 §8.6.5).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptUpperLimitInteger { get; } = Analog(0x6424, "Analog input interrupt upper limit integer", Cia401FunctionBlock.AnalogInput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.5");

    /// <summary>Object <c>6425h</c>: Analog input interrupt lower limit integer (CiA 401-1 §8.6.6).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptLowerLimitInteger { get; } = Analog(0x6425, "Analog input interrupt lower limit integer", Cia401FunctionBlock.AnalogInput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.6");

    /// <summary>Object <c>6426h</c>: Analog input interrupt delta unsigned (CiA 401-1 §8.6.7).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptDeltaUnsigned { get; } = Analog(0x6426, "Analog input interrupt delta unsigned", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.7");

    /// <summary>Object <c>6427h</c>: Analog input interrupt negative delta unsigned (CiA 401-1 §8.6.8).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptNegativeDeltaUnsigned { get; } = Analog(0x6427, "Analog input interrupt negative delta unsigned", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.8");

    /// <summary>Object <c>6428h</c>: Analog input interrupt positive delta unsigned (CiA 401-1 §8.6.9).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptPositiveDeltaUnsigned { get; } = Analog(0x6428, "Analog input interrupt positive delta unsigned", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.9");

    /// <summary>Object <c>6429h</c>: Analog input interrupt upper limit float (CiA 401-1 §8.6.10).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptUpperLimitFloat { get; } = Analog(0x6429, "Analog input interrupt upper limit float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.10");

    /// <summary>Object <c>642Ah</c>: Analog input interrupt lower limit float (CiA 401-1 §8.6.11).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptLowerLimitFloat { get; } = Analog(0x642A, "Analog input interrupt lower limit float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.11");

    /// <summary>Object <c>642Bh</c>: Analog input interrupt delta float (CiA 401-1 §8.6.12).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptDeltaFloat { get; } = Analog(0x642B, "Analog input interrupt delta float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.12");

    /// <summary>Object <c>642Ch</c>: Analog input interrupt negative delta float (CiA 401-1 §8.6.13).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptNegativeDeltaFloat { get; } = Analog(0x642C, "Analog input interrupt negative delta float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.13");

    /// <summary>Object <c>642Dh</c>: Analog input interrupt positive delta float (CiA 401-1 §8.6.14).</summary>
    public static Cia401ObjectDefinition AnalogInputInterruptPositiveDeltaFloat { get; } = Analog(0x642D, "Analog input interrupt positive delta float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.14");

    /// <summary>Object <c>642Eh</c>: Analog input offset float (CiA 401-1 §8.6.15).</summary>
    public static Cia401ObjectDefinition AnalogInputOffsetFloat { get; } = Analog(0x642E, "Analog input offset float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.15");

    /// <summary>Object <c>642Fh</c>: Analog input pre-scaling float (CiA 401-1 §8.6.16).</summary>
    public static Cia401ObjectDefinition AnalogInputPreScalingFloat { get; } = Analog(0x642F, "Analog input pre-scaling float", Cia401FunctionBlock.AnalogInput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.6.16");

    /// <summary>Object <c>6430h</c>: Analog input SI unit (CiA 401-1 §8.6.17).</summary>
    public static Cia401ObjectDefinition AnalogInputSiUnit { get; } = Analog(0x6430, "Analog input SI unit", Cia401FunctionBlock.AnalogInput, OdDataType.Unsigned32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, null, "8.6.17");

    /// <summary>Object <c>6431h</c>: Analog input offset integer (CiA 401-1 §8.6.18).</summary>
    public static Cia401ObjectDefinition AnalogInputOffsetInteger { get; } = Analog(0x6431, "Analog input offset integer", Cia401FunctionBlock.AnalogInput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.6.18");

    /// <summary>Object <c>6432h</c>: Analog input pre-scaling integer (CiA 401-1 §8.6.19).</summary>
    public static Cia401ObjectDefinition AnalogInputPreScalingInteger { get; } = Analog(0x6432, "Analog input pre-scaling integer", Cia401FunctionBlock.AnalogInput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.6.19");

    // ---- Analog output set-ups, CiA 401-1 §8.7 (6440h is reserved, §8.7.1; the norm numbers 6445h as §8.7.8) ---

    /// <summary>Object <c>6441h</c>: Analog output offset float (CiA 401-1 §8.7.2).</summary>
    public static Cia401ObjectDefinition AnalogOutputOffsetFloat { get; } = Analog(0x6441, "Analog output offset float", Cia401FunctionBlock.AnalogOutput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.7.2");

    /// <summary>Object <c>6442h</c>: Analog output scaling float (CiA 401-1 §8.7.3).</summary>
    public static Cia401ObjectDefinition AnalogOutputScalingFloat { get; } = Analog(0x6442, "Analog output scaling float", Cia401FunctionBlock.AnalogOutput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.7.3");

    /// <summary>Object <c>6443h</c>: Analog output error mode (CiA 401-1 §8.7.4).</summary>
    public static Cia401ObjectDefinition AnalogOutputErrorMode { get; } = Analog(0x6443, "Analog output error mode", Cia401FunctionBlock.AnalogOutput, OdDataType.Unsigned8,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.7.4");

    /// <summary>Object <c>6444h</c>: Analog output error value integer (CiA 401-1 §8.7.5).</summary>
    public static Cia401ObjectDefinition AnalogOutputErrorValueInteger { get; } = Analog(0x6444, "Analog output error value integer", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.7.5");

    /// <summary>Object <c>6445h</c>: Analog output error value float (CiA 401-1 §8.7.8).</summary>
    public static Cia401ObjectDefinition AnalogOutputErrorValueFloat { get; } = Analog(0x6445, "Analog output error value float", Cia401FunctionBlock.AnalogOutput, OdDataType.Real32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.7.8");

    /// <summary>Object <c>6446h</c>: Analog output offset integer (CiA 401-1 §8.7.9).</summary>
    public static Cia401ObjectDefinition AnalogOutputOffsetInteger { get; } = Analog(0x6446, "Analog output offset integer", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, "8.7.9");

    /// <summary>Object <c>6447h</c>: Analog output scaling integer (CiA 401-1 §8.7.10).</summary>
    public static Cia401ObjectDefinition AnalogOutputScalingInteger { get; } = Analog(0x6447, "Analog output scaling integer", Cia401FunctionBlock.AnalogOutput, OdDataType.Integer32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, "8.7.10");

    /// <summary>Object <c>6450h</c>: Analog output SI unit (CiA 401-1 §8.7.11).</summary>
    public static Cia401ObjectDefinition AnalogOutputSiUnit { get; } = Analog(0x6450, "Analog output SI unit", Cia401FunctionBlock.AnalogOutput, OdDataType.Unsigned32,
        OdAccess.ReadWrite, Cia401PdoMapping.Optional, null, "8.7.11");

    // ---- General device profile objects ---------------------------------------------------------

    /// <summary>Object <c>67FFh</c>: Device type — the device-type word of the first logical device
    /// of a multiple-device module, in the way of <c>1000h</c> (CiA 401-1, general device profile
    /// objects clause; CiA 301 §7.5.2.1). The document gives no object or entry description for it
    /// (its text ends after one sentence), so the data type UNSIGNED32, the access <c>ro</c> and the
    /// absence of a PDO mapping and of a default value are those of <c>1000h</c> in CiA 301. The
    /// clause is cited by its title: the document numbers it "8.3.1", which is the number of
    /// <c>6200h</c> as well.</summary>
    public static Cia401ObjectDefinition DeviceType { get; } = Variable(0x67FF, "Device type",
        Cia401FunctionBlock.General, Cia401PdoMapping.No, null, "General device profile objects",
        dataType: OdDataType.Unsigned32, access: OdAccess.ReadOnly);

    // ---- The catalogue ----------------------------------------------------------------------------

    /// <summary>Every object of CiA 401-1 clause 8, ordered by index.</summary>
    public static IReadOnlyList<Cia401ObjectDefinition> All { get; } = BuildAll();

    private static readonly Dictionary<ushort, Cia401ObjectDefinition> ByIndex = All.ToDictionary(d => d.Index);

    private static IReadOnlyList<Cia401ObjectDefinition> BuildAll()
    {
        var singles = new[]
        {
            ReadDigitalInput8,
            PolarityDigitalInput8,
            DigitalFilterEnable8,
            GlobalInterruptEnableDigital8,
            DigitalInterruptMaskAnyChange8,
            DigitalInterruptMaskLowToHigh8,
            DigitalInterruptMaskHighToLow8,
            ReadDigitalInput16,
            PolarityDigitalInput16,
            DigitalFilterEnable16,
            InterruptMaskDigitalInputAnyChange16,
            InterruptMaskDigitalInputLowToHigh16,
            InterruptMaskDigitalInputHighToLow16,
            ReadDigitalInput32,
            PolarityDigitalInput32,
            DigitalFilterEnable32,
            InterruptMaskDigitalInputAnyChange32,
            InterruptMaskDigitalInputLowToHigh32,
            InterruptMaskDigitalInputHighToLow32,
            WriteDigitalOutput8,
            ChangePolarityDigitalOutput8,
            ErrorModeDigitalOutput8,
            ErrorValueDigitalOutput8,
            FilterMaskDigitalOutput8,
            WriteDigitalOutput16,
            ChangePolarityDigitalOutput16,
            ErrorModeDigitalOutput16,
            ErrorValueDigitalOutput16,
            FilterMaskDigitalOutput16,
            WriteDigitalOutput32,
            ChangePolarityDigitalOutput32,
            ErrorModeDigitalOutput32,
            ErrorValueDigitalOutput32,
            FilterMaskDigitalOutput32,
            ReadAnalogInput8,
            ReadAnalogInput16,
            ReadAnalogInput32,
            ReadAnalogInputFloat,
            ReadManufacturerSpecificAnalogInput,
            WriteAnalogOutput8,
            WriteAnalogOutput16,
            WriteAnalogOutput32,
            WriteAnalogOutputFloat,
            WriteManufacturerSpecificAnalogOutput,
            AnalogInputInterruptTriggerSelection,
            AnalogInputInterruptSource,
            AnalogInputGlobalInterruptEnable,
            AnalogInputInterruptUpperLimitInteger,
            AnalogInputInterruptLowerLimitInteger,
            AnalogInputInterruptDeltaUnsigned,
            AnalogInputInterruptNegativeDeltaUnsigned,
            AnalogInputInterruptPositiveDeltaUnsigned,
            AnalogInputInterruptUpperLimitFloat,
            AnalogInputInterruptLowerLimitFloat,
            AnalogInputInterruptDeltaFloat,
            AnalogInputInterruptNegativeDeltaFloat,
            AnalogInputInterruptPositiveDeltaFloat,
            AnalogInputOffsetFloat,
            AnalogInputPreScalingFloat,
            AnalogInputSiUnit,
            AnalogInputOffsetInteger,
            AnalogInputPreScalingInteger,
            AnalogOutputOffsetFloat,
            AnalogOutputScalingFloat,
            AnalogOutputErrorMode,
            AnalogOutputErrorValueInteger,
            AnalogOutputErrorValueFloat,
            AnalogOutputOffsetInteger,
            AnalogOutputScalingInteger,
            AnalogOutputSiUnit,
            DeviceType
        };
        var ranges = new[]
        {
            ReadDigitalInputBit,
            PolarityDigitalInputBit,
            FilterConstantDigitalInputBit,
            InterruptMaskDigitalInputBitAnyChange,
            InterruptMaskDigitalInputBitLowToHigh,
            InterruptMaskDigitalInputBitHighToLow,
            WriteDigitalOutputBit,
            ChangePolarityDigitalOutputBit,
            ErrorModeDigitalOutputBit,
            ErrorValueDigitalOutputBit,
            FilterMaskDigitalOutputBit
        };

        return singles.Concat(ranges.SelectMany(r => r)).OrderBy(d => d.Index).ToList().AsReadOnly();
    }

    /// <summary>Looks an object up by its index in logical device 0.</summary>
    /// <returns><see langword="false"/> for an index the norm does not define, which includes the
    /// reserved <c>6420h</c> and <c>6440h</c>.</returns>
    public static bool TryGet(ushort indexInDevice0, out Cia401ObjectDefinition definition)
    {
        if (ByIndex.TryGetValue(indexInDevice0, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    // ---- Describe ---------------------------------------------------------------------------------

    /// <summary>
    /// Names what a sub-index of a profile object carries, for logs and diagnostics:
    /// <c>Describe(0x6000, 2)</c> is <c>"Read digital input 8-bit, inputs 9–16"</c>. The channels of
    /// a digital object follow CiA 401-1 §7.2.1.3: sub-index = (channel − 1) div width + 1, with the
    /// width of the object in bits, so sub-index 2 of an 8-bit object carries channels 9 to 16.
    /// </summary>
    /// <param name="indexInDevice0">The object index in logical device 0.</param>
    /// <param name="subindex">The sub-index.</param>
    /// <returns>The description; for a sub-index the norm does not define, or an index that is not
    /// an object of the profile, the index and sub-index with a remark to that effect.</returns>
    public static string Describe(ushort indexInDevice0, byte subindex)
    {
        if (!TryGet(indexInDevice0, out var d))
            return string.Format(CultureInfo.InvariantCulture, "{0:X4}h sub-index {1:X2}h (not a CiA 401 object)", indexInDevice0, subindex);

        if (subindex == 0)
            return d.IsArray ? d.Name + ", highest sub-index" : d.Name;

        if (!d.IsArray || subindex > d.MaxSubindex)
            return string.Format(CultureInfo.InvariantCulture, "{0}, sub-index {1:X2}h (not defined)", d.Name, subindex);

        // 8-, 16- and 32-bit digital objects: a group of channels per sub-index.
        if (d.BitsPerElement > 1)
        {
            int first = (subindex - 1) * d.BitsPerElement + 1;
            int last = subindex * d.BitsPerElement;
            return string.Format(CultureInfo.InvariantCulture, "{0}, {1}s {2}–{3}", d.Name, d.ElementNoun, first, last);
        }

        // Single-channel objects count from the first channel of their range; the analog objects
        // count from 1 (one channel, or one bank, per sub-index).
        int number = d.FirstChannel + subindex - 1;
        return string.Format(CultureInfo.InvariantCulture, "{0}, {1} {2}", d.Name, d.ElementNoun, number);
    }

    // ---- Builders ---------------------------------------------------------------------------------

    private static string NounOf(Cia401FunctionBlock block) => block switch
    {
        Cia401FunctionBlock.DigitalInput => "input",
        Cia401FunctionBlock.DigitalOutput => "output",
        Cia401FunctionBlock.AnalogInput => "analog input",
        Cia401FunctionBlock.AnalogOutput => "analog output",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "The block has no channels."),
    };

    private static OdDataType DigitalTypeOf(int bits) => bits switch
    {
        1 => OdDataType.Boolean,
        8 => OdDataType.Unsigned8,
        16 => OdDataType.Unsigned16,
        32 => OdDataType.Unsigned32,
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "A digital object carries 1, 8, 16 or 32 channels per sub-index."),
    };

    /// <summary>An 8-, 16- or 32-bit digital array.</summary>
    private static Cia401ObjectDefinition Digital(
        ushort index, string name, Cia401FunctionBlock block, int bits,
        OdAccess access, Cia401PdoMapping pdo, long? defaultValue, string clause)
        => new(index, name, block, isArray: true, DigitalTypeOf(bits), isManufacturerSpecific: false, access, pdo,
            defaultValue, bits, firstChannel: 1, HighestArraySubindex, NounOf(block), clause);

    /// <summary>The eight objects of a range: 128 single channels per index, the first of them
    /// starting at channel 1 and each following one 128 channels further.</summary>
    private static IReadOnlyList<Cia401ObjectDefinition> Range(
        ushort first, string name, Cia401FunctionBlock block,
        OdAccess access, Cia401PdoMapping pdo, long? defaultValue, string clause)
    {
        var list = new Cia401ObjectDefinition[8];
        for (int k = 0; k < list.Length; k++)
        {
            int firstChannel = 128 * k + 1;
            string range = string.Format(CultureInfo.InvariantCulture, "{0} {1} to {2}", name, firstChannel, firstChannel + 127);
            list[k] = new(checked((ushort)(first + k)), range, block, isArray: true, OdDataType.Boolean,
                isManufacturerSpecific: false, access, pdo, defaultValue, bitsPerElement: 1, firstChannel,
                HighestRangeSubindex, NounOf(block), clause);
        }

        return Array.AsReadOnly(list);
    }

    /// <summary>An analog array: one channel per sub-index.</summary>
    private static Cia401ObjectDefinition Analog(
        ushort index, string name, Cia401FunctionBlock block, OdDataType dataType,
        OdAccess access, Cia401PdoMapping pdo, long? defaultValue, string clause,
        bool manufacturerSpecific = false, string? noun = null, byte maxSubindex = HighestArraySubindex)
        => new(index, name, block, isArray: true, dataType, manufacturerSpecific, access, pdo, defaultValue,
            bitsPerElement: 0, firstChannel: 1, maxSubindex, noun ?? NounOf(block), clause);

    /// <summary>A <c>VAR</c> at sub-index 0: <c>BOOLEAN</c> read-write unless told otherwise.</summary>
    private static Cia401ObjectDefinition Variable(
        ushort index, string name, Cia401FunctionBlock block, Cia401PdoMapping pdo, long? defaultValue, string clause,
        OdDataType dataType = OdDataType.Boolean, OdAccess access = OdAccess.ReadWrite)
        => new(index, name, block, isArray: false, dataType, isManufacturerSpecific: false, access, pdo, defaultValue,
            bitsPerElement: 0, firstChannel: 0, maxSubindex: 0, elementNoun: string.Empty, clause);
}
