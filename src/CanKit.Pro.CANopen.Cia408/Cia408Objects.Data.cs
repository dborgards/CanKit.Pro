using System;
using System.Linq;
using static CanKit.Pro.CANopen.Cia408.Cia408Area;
using static CanKit.Pro.CANopen.OdDataType;

namespace CanKit.Pro.CANopen.Cia408;

// The object table of CiA 408 v2.2.0 clause 9, one line per object, in the order of the norm:
//   Var(index, name, clause, area, type, SDO access, PDO access, category, default)
//   Rec(index, name, clause, area, value type, SDO access, PDO access, category, default, unit, prefix)
// Access, PDO access and default are those of the VAR or of sub-index 01h of the value record.
// Default tokens: V(n) a value, No "No", Ms "Manufacturer-specific", Ds "Device specific",
// Cms "(control mode specific)", Ts "See Table 12", Ir / Ir32 internal resolution (7.3),
// U(code) a CiA 303-2 unit, P(n) a prefix (power of ten). See Cia408Objects.cs for the builders.
internal static partial class Cia408Objects
{
    static Cia408Objects()
    {
        var table = new[]
        {
            // ---- §9.3 Device control ----
            DeviceControlWord16 = Var(0x6040, "Device control word 16-bit", "9.3.2", DeviceControl, Unsigned16, Acc.Rw, Pdo.TrOrR, Mandatory, V(0)), // heading: "Device control word"
            DeviceStatusWord16 = Var(0x6041, "Device status word 16-bit", "9.3.3", DeviceControl, Unsigned16, Acc.Ro, Pdo.T, Mandatory, No),
            DeviceMode = Var(0x6042, "Device mode", "9.3.4", DeviceControl, Integer8, Acc.RwOrRo, Pdo.TrROrNo, Mandatory, V(1)), // default 01h if rw, none if ro
            DeviceControlMode = Var(0x6043, "Device control mode", "9.3.5", DeviceControl, Integer8, Acc.RwOrRo, Pdo.TrROrNo, Optional, Ms),
            DeviceControlWord32 = Var(0x6044, "Device control word 32-bit", "9.3.6", DeviceControl, Unsigned32, Acc.Rw, Pdo.TrOrR, Mandatory, V(0)),
            DeviceStatusWord32 = Var(0x6045, "Device status word 32-bit", "9.3.7", DeviceControl, Unsigned32, Acc.Ro, Pdo.T, Mandatory, No),
            DeviceErrorCode = Var(0x604E, "Device error code", "9.3.8", DeviceControl, Unsigned16, Acc.Ro, Pdo.T, Optional, No),
            DeviceLocal = Var(0x604F, "Device local", "9.3.9", DeviceControl, Integer8, Acc.RwOrRo, Pdo.TrROrNo, Optional, ByDeviceKind), // default 0 valve or pump, 1 drive; "constant" although rw

            // ---- §9.4 Device information ----
            Var(0x6050, "Device version", "9.4.1", DeviceInformation, VisibleString, Acc.Ro, Pdo.No, Optional, Ms),
            Var(0x6051, "Device code number", "9.4.2", DeviceInformation, Unsigned16, Acc.Rw, Pdo.TOrNo, Optional, V(0)),
            Var(0x6052, "Device serial number", "9.4.3", DeviceInformation, VisibleString, Acc.Ro, Pdo.TOrNo, Optional, Ms),
            Var(0x6053, "Device description", "9.4.4", DeviceInformation, VisibleString, Acc.Rw, Pdo.No, Optional, Text("0")), // a VISIBLE_STRING64 with the default "0"
            Var(0x6054, "Device model description", "9.4.5", DeviceInformation, VisibleString, Acc.Ro, Pdo.No, Optional, Ms),
            Var(0x6055, "Device model URL", "9.4.6", DeviceInformation, VisibleString, Acc.Ro, Pdo.No, Optional, Ms),
            Var(0x6056, "Device parameter set code", "9.4.7", DeviceInformation, Unsigned8, Acc.Rw, Pdo.TOrNo, Optional, V(0)),
            Var(0x6057, "Device vendor name", "9.4.8", DeviceInformation, VisibleString, Acc.Ro, Pdo.No, Optional, Ms),

            // ---- §9.5 Actual value conditioning, valves and pumps ----
            Var(0x6100, "vlv actual value conditioning max interface number", "9.5.2", ValveActualValueConditioning, Unsigned8, Acc.Ro, Pdo.TOrNo, If("actual value conditioning is implemented"), V(1)),
            Var(0x6101, "vlv actual value conditioning interface number", "9.5.3", ValveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented"), V(1)),
            Var(0x6102, "vlv actual value conditioning type", "9.5.4", ValveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented"), V(0)),
            Var(0x6103, "vlv actual value conditioning sign", "9.5.5", ValveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6104, "vlv actual value conditioning actual value", "9.5.6", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6105, "vlv actual value conditioning actual value 32-bit", "9.5.7", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6110, "vlv actual value conditioning actual value 1", "9.5.8", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6111, "vlv actual value conditioning actual value 2", "9.5.9", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6112, "vlv actual value conditioning actual value 3", "9.5.10", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6113, "vlv actual value conditioning actual value 4", "9.5.11", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6114, "vlv actual value conditioning actual value 5", "9.5.12", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6115, "vlv actual value conditioning actual value 6", "9.5.13", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6116, "vlv actual value conditioning actual value 7", "9.5.14", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6117, "vlv actual value conditioning actual value 8", "9.5.15", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6118, "vlv actual value conditioning actual value 1 32-bit", "9.5.16", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6119, "vlv actual value conditioning actual value 2 32-bit", "9.5.17", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611A, "vlv actual value conditioning actual value 3 32-bit", "9.5.18", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611B, "vlv actual value conditioning actual value 4 32-bit", "9.5.19", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611C, "vlv actual value conditioning actual value 5 32-bit", "9.5.20", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611D, "vlv actual value conditioning actual value 6 32-bit", "9.5.21", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611E, "vlv actual value conditioning actual value 7 32-bit", "9.5.22", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x611F, "vlv actual value conditioning actual value 8 32-bit", "9.5.23", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6120, "vlv actual value conditioning min pressure", "9.5.24", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), No, U(0x4E), P(-3)),
            Rec(0x6121, "vlv actual value conditioning max pressure", "9.5.25", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), No, U(0x4E), P(-3)),
            Rec(0x6122, "vlv actual value conditioning area", "9.5.26", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), No, U(0xA7), P(-6)), // unit "A7h (m², or 58h …)"
            Rec(0x6123, "vlv actual value conditioning pressure offset", "9.5.27", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), No, U(0x4E), P(0)),
            Rec(0x6124, "vlv actual value conditioning min transducer signal", "9.5.28", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), No, U(0x26), P(0)),
            Rec(0x6125, "vlv actual value conditioning max transducer signal", "9.5.29", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(10), U(0x26), P(0)),
            Rec(0x6128, "vlv actual value conditioning min general transducer", "9.5.30", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), V(0), U(0x26), P(0)),
            Rec(0x6129, "vlv actual value conditioning max general transducer", "9.5.31", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), V(0), U(0x26), P(0)), // PDO cell read by hand (the SRDO cell prints "rw")
            Rec(0x612B, "vlv actual value conditioning general transducer offset", "9.5.32", ValveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), V(0), U(0x26), P(0)),
            Rec(0x612C, "vlv actual value conditioning min general transducer 32-bit", "9.5.33", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit actual value conditioning is implemented and transducer type is 5"), V(0), U(0x26), P(0)),
            Rec(0x612D, "vlv actual value conditioning max general transducer 32-bit", "9.5.34", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit actual value conditioning is implemented and transducer type is 5"), V(10), U(0x26), P(0)),
            Rec(0x6130, "vlv actual value conditioning min reference", "9.5.35", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x01), P(-6)), // Annex A: INTEGER16
            Rec(0x6131, "vlv actual value conditioning max reference", "9.5.36", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x01), P(-6)), // Annex A: INTEGER16
            Rec(0x6132, "vlv actual value conditioning T1", "9.5.37", ValveActualValueConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x03), P(-3)),
            Rec(0x6133, "vlv actual value conditioning min interface", "9.5.38", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(10), U(0x26), P(0)),
            Rec(0x6134, "vlv actual value conditioning max interface", "9.5.39", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(10), U(0x26), P(0)),
            Rec(0x6140, "vlv actual value conditioning resolution", "9.5.40", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 64 to 66, 69 or 70"), V(0), U(0x01), P(-6)),
            Rec(0x6141, "vlv actual value conditioning position offset", "9.5.41", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 65, 66, 69 or 70"), V(0), U(0x01), P(-6)),
            Rec(0x6142, "vlv actual value conditioning zero shift", "9.5.42", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 64"), V(0), U(0x01), P(-6)),
            Var(0x6143, "vlv actual value conditioning bit size", "9.5.43", ValveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 65 or 66"), V(24)),
            Rec(0x6144, "vlv actual value conditioning C", "9.5.44", ValveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 68"), V(0), U(0x03), P(-3)),
            Var(0x6145, "vlv actual value conditioning start stop type", "9.5.45", ValveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 68"), Ms),
            Rec(0x6146, "vlv pressure actual value referenced by interface A", "9.5.46", ValveActualValueConditioning, Unsigned8, Acc.Ro, Pdo.T, If("actual value conditioning is implemented and transducer type is 3"), No, Ms, Ms),
            Rec(0x6147, "vlv pressure actual value referenced by interface B", "9.5.47", ValveActualValueConditioning, Unsigned8, Acc.Ro, Pdo.T, If("actual value conditioning is implemented and transducer type is 3"), No, Ms, Ms),
            Rec(0x6170, "vlv physical (raw) value 1 16-bit", "9.5.48", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6171, "vlv physical (raw) value 2 16-bit", "9.5.49", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6172, "vlv physical (raw) value 3 16-bit", "9.5.50", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6173, "vlv physical (raw) value 4 16-bit", "9.5.51", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6174, "vlv physical (raw) value 5 16-bit", "9.5.52", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6175, "vlv physical (raw) value 6 16-bit", "9.5.53", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6176, "vlv physical (raw) value 7 16-bit", "9.5.54", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6177, "vlv physical (raw) value 8 16-bit", "9.5.55", ValveActualValueConditioning, Integer16, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6178, "vlv physical (raw) value 1 32-bit", "9.5.56", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6179, "vlv physical (raw) value 2 32-bit", "9.5.57", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617A, "vlv physical (raw) value 3 32-bit", "9.5.58", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617B, "vlv physical (raw) value 4 32-bit", "9.5.59", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617C, "vlv physical (raw) value 5 32-bit", "9.5.60", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617D, "vlv physical (raw) value 6 32-bit", "9.5.61", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617E, "vlv physical (raw) value 7 32-bit", "9.5.62", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x617F, "vlv physical (raw) value 8 32-bit", "9.5.63", ValveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),

            // ---- §9.6 Actual value conditioning, drives ----
            Var(0x6200, "drv actual value conditioning max interface number", "9.6.2", DriveActualValueConditioning, Unsigned8, Acc.Ro, Pdo.T, If("actual value conditioning is implemented"), No),
            Var(0x6201, "drv actual value conditioning interface number", "9.6.3", DriveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented"), V(1)),
            Var(0x6202, "drv actual value conditioning type", "9.6.4", DriveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented"), V(0)),
            Var(0x6203, "drv actual value conditioning sign", "9.6.5", DriveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6204, "drv actual value conditioning actual value", "9.6.6", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6210, "drv actual value conditioning actual value 1", "9.6.7", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6211, "drv actual value conditioning actual value 2", "9.6.8", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6212, "drv actual value conditioning actual value 3", "9.6.9", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6213, "drv actual value conditioning actual value 4", "9.6.10", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6214, "drv actual value conditioning actual value 5", "9.6.11", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6215, "drv actual value conditioning actual value 6", "9.6.12", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6216, "drv actual value conditioning actual value 7", "9.6.13", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6217, "drv actual value conditioning actual value 8", "9.6.14", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ts, Ts),
            Rec(0x6220, "drv actual value conditioning min pressure", "9.6.15", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(0), U(0x4E), P(-3)),
            Rec(0x6221, "drv actual value conditioning max pressure", "9.6.16", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(0), U(0x4E), P(-3)),
            Rec(0x6222, "drv actual value conditioning area", "9.6.17", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), Ms, U(0xA7), P(-6)), // value range of 01h says INTEGER16
            Rec(0x6223, "drv actual value conditioning pressure offset", "9.6.18", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(0), U(0x4E), P(0)),
            Rec(0x6224, "drv actual value conditioning min transducer signal", "9.6.19", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(0), U(0x26), P(0)),
            Rec(0x6225, "drv actual value conditioning max transducer signal", "9.6.20", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 2"), V(10), U(0x26), P(0)),
            Rec(0x6228, "drv actual value conditioning min general transducer", "9.6.21", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), No, U(0x26), P(0)),
            Rec(0x6229, "drv actual value conditioning max general transducer", "9.6.22", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), No, U(0x26), P(0)),
            Rec(0x622B, "drv actual value conditioning general transducer offset", "9.6.23", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 5"), No, U(0x26), P(0)),
            Rec(0x622C, "drv actual value conditioning min general transducer 32-bit", "9.6.24", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit actual value conditioning is implemented and transducer type is 5"), No, U(0x26), P(0)),
            Rec(0x622D, "drv actual value conditioning max general transducer 32-bit", "9.6.25", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit actual value conditioning is implemented and transducer type is 5"), V(10), U(0x26), P(0)),
            Rec(0x6230, "drv actual value conditioning min reference", "9.6.26", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x01), P(-6)),
            Rec(0x6231, "drv actual value conditioning max reference", "9.6.27", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x01), P(-6)),
            Rec(0x6232, "drv actual value conditioning T1", "9.6.28", DriveActualValueConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(0), U(0x03), P(-3)),
            Rec(0x6233, "drv actual value conditioning min interface", "9.6.29", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(-10), U(0x26), P(0)),
            Rec(0x6234, "drv actual value conditioning max interface", "9.6.30", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 67"), V(10), U(0x26), P(0)),
            Rec(0x6240, "drv actual value conditioning resolution", "9.6.31", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 64 to 66, 69 or 70"), V(0), U(0x01), P(-6)),
            Rec(0x6241, "drv actual value conditioning position offset", "9.6.32", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 65, 66, 69 or 70"), V(0), U(0x01), P(-6)),
            Rec(0x6242, "drv actual value conditioning zero shift", "9.6.33", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 64"), V(0), U(0x01), P(-6)),
            Var(0x6243, "drv actual value conditioning bit size", "9.6.34", DriveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 65 or 66"), V(24)), // Annex A: Optional
            Rec(0x6244, "drv actual value conditioning C", "9.6.35", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 68"), V(0), U(0x03), P(-3)),
            Var(0x6245, "drv actual value conditioning start stop type", "9.6.36", DriveActualValueConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 68"), Ms),
            Rec(0x6246, "drv pressure actual value referenced by interface A", "9.6.37", DriveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 3"), No, Ms, Ms),
            Rec(0x6247, "drv pressure actual value referenced by interface B", "9.6.38", DriveActualValueConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 3"), No, Ms, Ms),
            Rec(0x6250, "drv position value for position transducer fieldbus", "9.6.39", DriveActualValueConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 70"), No, U(0x01), P(-6)),
            Rec(0x6251, "drv velocity value for velocity transducer fieldbus", "9.6.40", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 71"), No, Ms, Ms),
            Rec(0x6252, "drv velocity resolution value for velocity transducer fieldbus", "9.6.41", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 71"), No, U(0xA0), P(-6)),
            Rec(0x6253, "drv velocity offset for velocity transducer fieldbus", "9.6.42", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 71"), No, U(0xA0), P(-6)),
            Rec(0x6254, "drv acceleration value for acceleration transducer fieldbus", "9.6.43", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 72"), No, Ms, Ms), // sub-index 00h default "No"
            Rec(0x6255, "drv acceleration resolution value for acceleration transducer fieldbus", "9.6.44", DriveActualValueConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 72"), No, U(0xA8), P(0)),
            Rec(0x6256, "drv acceleration offset for acceleration transducer fieldbus", "9.6.45", DriveActualValueConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("actual value conditioning is implemented and transducer type is 72"), V(0), U(0xA8), P(0)), // Annex A: Optional
            Rec(0x6278, "drv physical (raw) value 1 32-bit", "9.6.46", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x6279, "drv physical (raw) value 2 32-bit", "9.6.47", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627A, "drv physical (raw) value 3 32-bit", "9.6.48", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627B, "drv physical (raw) value 4 32-bit", "9.6.49", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627C, "drv physical (raw) value 5 32-bit", "9.6.50", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627D, "drv physical (raw) value 6 32-bit", "9.6.51", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627E, "drv physical (raw) value 7 32-bit", "9.6.52", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),
            Rec(0x627F, "drv physical (raw) value 8 32-bit", "9.6.53", DriveActualValueConditioning, Integer32, Acc.Ro, Pdo.T, Optional, No, Ms, Ms),

            // ---- §9.7 Actuator conditioning 16-bit and 32-bit ----
            Rec(0x6281, "actuator conditioning interface min 32-bit", "9.7.3", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("an actuator conditioning interface is implemented"), Ms, Ms, Ms), // sub-index 00h default "No"
            Rec(0x6282, "actuator conditioning interface max 32-bit", "9.7.4", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("an actuator conditioning interface is implemented"), No, Ms, Ms),
            Var(0x6290, "actuator conditioning filter type 32-bit", "9.7.5", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("actuator conditioning filter is implemented"), V(0)),
            Rec(0x6291, "actuator conditioning filter T1 32-bit", "9.7.6", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("filter type 1 is implemented"), V(0), U(0x03), P(-3)),
            Rec(0x6292, "actuator conditioning filter D 32-bit", "9.7.7", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("filter type 2 is implemented"), V(0), U(0x00), P(0)),
            Rec(0x6293, "actuator conditioning filter f0 32-bit", "9.7.8", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("filter type 2 is implemented"), V(0), U(0x20), P(-3)),
            Var(0x6294, "actuator conditioning Butterworth filter order 32-bit", "9.7.9", ActuatorConditioning, Unsigned8, Acc.Rw, Pdo.TrROrNo, If("filter type 3 is implemented"), V(0)),
            Rec(0x6295, "actuator conditioning Butterworth filter f0 32-bit", "9.7.10", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("filter type 2 is implemented"), V(0), U(0x20), P(0)),
            Var(0x62A0, "actuator conditioning directional dependent gain type 32-bit", "9.7.11", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain function is implemented"), V(0)),
            Var(0x62A1, "actuator conditioning directional dependent gain factor 32-bit", "9.7.12", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain factor type 1 is implemented"), V(65537)),
            Var(0x62A2, "actuator conditioning characteristic compensation type 32-bit", "9.7.13", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("compensation function is implemented"), V(0)),
            Rec(0x62A3, "actuator conditioning directional dependent gain factor 2 32-bit", "9.7.14", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain factor type 2 is implemented"), V(1000), U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Var(0x62B0, "actuator conditioning dead band compensation type 32-bit", "9.7.15", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("dead band compensation function is implemented"), V(0)),
            Rec(0x62B1, "actuator conditioning dead band compensation A side 32-bit", "9.7.16", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), V(0), U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Rec(0x62B2, "actuator conditioning dead band compensation B side 32-bit", "9.7.17", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), V(0), U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Rec(0x62B3, "actuator conditioning dead band compensation threshold 32-bit", "9.7.18", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), Ds, U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Rec(0x62C0, "actuator conditioning zero correction offset 32-bit", "9.7.19", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("zero correction function is implemented"), V(0), U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Var(0x62D0, "actuator conditioning dither type 32-bit", "9.7.20", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("dither function is implemented"), V(0)),
            Rec(0x62D1, "actuator conditioning dither amplitude 32-bit", "9.7.21", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("dither function type is 1, 2 or 3"), V(0), U(0x00), P(-6)), // prefix printed "FAh, mili": the code is taken (see DefaultPrefix)
            Rec(0x62D2, "actuator conditioning dither frequency 32-bit", "9.7.22", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("dither function type is 1, 2 or 3"), V(0), U(0x20), P(0)),
            Rec(0x62E0, "actuator conditioning upper limit 32-bit", "9.7.23", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(0), Cms, Cms),
            Rec(0x62E1, "actuator conditioning lower limit 32-bit", "9.7.24", ActuatorConditioning, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(0), Cms, Cms),
            Var(0x62F0, "actuator conditioning inverting sign 32-bit", "9.7.25", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6324, "actuator conditioning zero correction offset", "9.7.26", ActuatorConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("zero correction function is implemented"), V(0), Ir, P(0)), // sub-index 00h default "No"
            Var(0x6340, "actuator conditioning directional dependent gain type", "9.7.27", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain function is implemented"), V(0)),
            Var(0x6341, "actuator conditioning directional dependent gain type 1 factor", "9.7.28", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain type is 1"), V(65537)),
            Var(0x6342, "actuator conditioning dead band compensation type", "9.7.29", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("dead band compensation is implemented"), V(0)),
            Rec(0x6343, "actuator conditioning dead band compensation A side", "9.7.30", ActuatorConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), V(0), Ir, P(0)),
            Rec(0x6344, "actuator conditioning dead band compensation B side", "9.7.31", ActuatorConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), V(0), Ir, P(0)),
            Rec(0x6345, "actuator conditioning dead band compensation threshold", "9.7.32", ActuatorConditioning, Integer16, Acc.Rw, Pdo.TrROrNo, If("dead band compensation type is 1 or 2"), Ds, Ir, P(0)),
            Var(0x6346, "actuator conditioning characteristic compensation type", "9.7.33", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("compensation function is implemented"), V(0)),
            Rec(0x6347, "actuator conditioning directional dependent gain type 2 factor", "9.7.34", ActuatorConditioning, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("directional dependent gain type is 2"), V(1000), U(0x00), P(-3)), // sub-index 00h rw, variable, default "No"; Annex A: INTEGER32
            Var(0x6360, "dither type", "9.7.35", ActuatorConditioning, Integer8, Acc.Rw, Pdo.TrROrNo, If("dither function is implemented"), V(0)),
            Rec(0x6361, "dither amplitude", "9.7.36", ActuatorConditioning, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("dither function type is 1"), V(0), Ir, P(0)),
            Rec(0x6362, "dither frequency", "9.7.37", ActuatorConditioning, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("dither function type is 1"), V(0), U(0x20), P(0)),

            // ---- §9.8 Spool position control (vpoc): process values ----
            QSetpoint = Rec(0x6300, "q setpoint", "9.8.2", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 1 or 2"), V(0), Ir, P(0)),
            SpoolPositionActualValue = Rec(0x6301, "Spool position actual value", "9.8.3", ValveSpoolPosition, Integer16, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 2"), No, Ir, P(0)),
            Var(0x6302, "vpoc interface reference", "9.8.4", ValveSpoolPosition, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            QSetpoint32 = Rec(0x6308, "q setpoint 32-bit", "9.8.5", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit parameters and control mode is 1 or 2"), V(0), Ir32, P(0)),
            SpoolPositionActualValue32 = Rec(0x6309, "Spool actual value 32-bit", "9.8.6", ValveSpoolPosition, Integer32, Acc.Ro, Pdo.T, If("32-bit parameters and control mode is 2"), No, Ir, P(0)), // object description: "Spool actual value 32-bit"; unit "ir"
            Rec(0x630A, "q demand value 32-bit", "9.8.7", ValveSpoolPosition, Integer32, Acc.Ro, Pdo.T, Optional, V(0), Cms, Cms),
            Rec(0x6310, "q demand value", "9.8.8", ValveSpoolPosition, Integer16, Acc.Ro, Pdo.T, Optional, V(0), Cms, Cms),
            Rec(0x6312, "Spool position setpoint", "9.8.9", ValveSpoolPosition, Integer16, Acc.Ro, Pdo.TOrNo, Optional, V(0), Ir, P(0)), // Annex A: rw, TR, R or No
            Rec(0x6313, "Spool position demand value", "9.8.10", ValveSpoolPosition, Integer16, Acc.Ro, Pdo.TOrNo, Optional, V(0), Ir, P(0)), // Annex A: rw, TR, R or No
            Rec(0x631E, "Spool position setpoint 32-bit", "9.8.11", ValveSpoolPosition, Integer32, Acc.Ro, Pdo.TOrNo, Optional, V(0), Ir, P(0)), // Annex A: rw, TR, R or No
            Rec(0x631F, "Spool position demand value 32-bit", "9.8.12", ValveSpoolPosition, Integer32, Acc.Ro, Pdo.TOrNo, Optional, V(0), Ir, P(0)), // Annex A: rw, TR, R or No
            Rec(0x6350, "vpoc control deviation", "9.8.13", ValveSpoolPosition, Integer16, Acc.Ro, Pdo.T, Optional, No, Ir, P(0)),
            Rec(0x635B, "vpoc control deviation 32-bit", "9.8.14", ValveSpoolPosition, Integer32, Acc.Ro, Pdo.T, Optional, No, Ir32, P(0)),

            // ---- §9.9 vpoc setpoint conditioning ----
            Rec(0x6311, "vpoc setpoint conditioning reference value", "9.9.2", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(16384), Ir, P(0)),
            Rec(0x6314, "vpoc setpoint conditioning hold setpoint", "9.9.3", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir, P(0)), // sub-index 01h printed "^"
            Rec(0x6318, "vpoc setpoint conditioning reference value 32-bit", "9.9.4", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(100000), Ir32, P(0)),
            Rec(0x631B, "vpoc setpoint conditioning hold setpoint 32-bit", "9.9.5", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir32, P(0)),
            Rec(0x6320, "vpoc setpoint conditioning upper setpoint limit", "9.9.6", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(32767), Ir, P(0)),
            Rec(0x6321, "vpoc setpoint conditioning lower setpoint limit", "9.9.7", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-32768), Ir, P(0)),
            Var(0x6322, "vpoc setpoint conditioning scaling factor", "9.9.8", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), V(65537)),
            Rec(0x6323, "vpoc setpoint conditioning scaling offset", "9.9.9", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), Ms, Ir, P(0)), // sub-index 00h default "No"
            Rec(0x6328, "vpoc setpoint conditioning upper setpoint limit 32-bit", "9.9.10", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit limit function is implemented"), V(2147483647), Ir32, P(0)),
            Rec(0x6329, "vpoc setpoint conditioning lower setpoint limit 32-bit", "9.9.11", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit limit function is implemented"), V(-2147483648), Ir32, P(0)),
            Var(0x6330, "vpoc setpoint conditioning ramp type", "9.9.12", ValveSpoolPosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x6331, "vpoc setpoint conditioning ramp acceleration time", "9.9.13", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6332, "vpoc setpoint conditioning ramp acceleration time positive", "9.9.14", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6333, "vpoc setpoint conditioning ramp acceleration time negative", "9.9.15", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6334, "vpoc setpoint conditioning ramp deceleration time", "9.9.16", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x6335, "vpoc setpoint conditioning ramp deceleration time positive", "9.9.17", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6336, "vpoc setpoint conditioning ramp deceleration time negative", "9.9.18", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6338, "vpoc setpoint conditioning ramp acceleration time 32-bit", "9.9.19", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6339, "vpoc setpoint conditioning ramp acceleration time positive 32-bit", "9.9.20", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x633A, "vpoc setpoint conditioning ramp acceleration time negative 32-bit", "9.9.21", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x633B, "vpoc setpoint conditioning ramp deceleration time 32-bit", "9.9.22", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x633C, "vpoc setpoint conditioning ramp deceleration time positive 32-bit", "9.9.23", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x633D, "vpoc setpoint conditioning ramp deceleration time negative 32-bit", "9.9.24", ValveSpoolPosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.10 vpoc control monitoring ----
            Var(0x6351, "vpoc control monitoring type", "9.10.2", ValveSpoolPosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)),
            Rec(0x6352, "vpoc control monitoring delay time", "9.10.3", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6353, "vpoc control monitoring symmetric threshold", "9.10.4", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ms, Cms, Cms),
            Rec(0x6354, "vpoc control monitoring upper threshold", "9.10.5", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6355, "vpoc control monitoring lower threshold", "9.10.6", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6358, "vpoc control monitoring threshold 32-bit", "9.10.7", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6359, "vpoc control monitoring upper threshold 32-bit", "9.10.7.1", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x635A, "vpoc control monitoring lower threshold 32-bit", "9.10.8", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.11 vpoc target window monitoring ----
            Var(0x6370, "vpoc target window monitoring type", "9.11.2", ValveSpoolPosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x6371, "vpoc target window monitoring switch on time", "9.11.3", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, Ds, U(0x03), P(-3)),
            Rec(0x6372, "vpoc target window monitoring switch off time", "9.11.4", ValveSpoolPosition, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, Ds, U(0x03), P(-3)),
            Rec(0x6373, "vpoc target window monitoring symmetric threshold", "9.11.5", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6374, "vpoc target window monitoring upper threshold", "9.11.6", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6375, "vpoc target window monitoring lower threshold", "9.11.7", ValveSpoolPosition, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6378, "vpoc target window monitoring threshold 32-bit", "9.11.8", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6379, "vpoc target window monitoring upper threshold 32-bit", "9.11.9", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x637A, "vpoc target window monitoring lower threshold 32-bit", "9.11.10", ValveSpoolPosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.12 Pressure control (vprc): process values ----
            VprcSetpoint = Rec(0x6380, "vprc setpoint", "9.12.2", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 3 or 4"), V(0), Ir, P(0)),
            VprcActualValue = Rec(0x6381, "vprc actual value", "9.12.3", ValvePressure, Integer16, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 4"), No, Ir, P(0)),
            Var(0x6382, "vprc interface reference", "9.12.4", ValvePressure, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            VprcSetpoint32 = Rec(0x6388, "vprc setpoint 32-bit", "9.12.5", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit parameters and control mode is 3 or 4"), V(0), Ir32, P(0)),
            VprcActualValue32 = Rec(0x6389, "vprc actual value 32-bit", "9.12.6", ValvePressure, Integer32, Acc.Ro, Pdo.T, If("32-bit parameters and control mode is 3 or 4"), V(0), Ir, P(0)), // default 0 although ro; unit "ir"
            Rec(0x638A, "vprc demand value 32-bit", "9.12.7", ValvePressure, Integer32, Acc.Ro, Pdo.T, Optional, V(0), Cms, Cms), // Annex A: Conditional
            Rec(0x6390, "vprc setpoint conditioning demand value", "9.12.8", ValvePressure, Integer16, Acc.Ro, Pdo.T, Optional, V(0), Cms, Cms),
            Rec(0x63D0, "vprc control deviation", "9.12.9", ValvePressure, Integer16, Acc.Ro, Pdo.T, Optional, No, Cms, Cms),

            // ---- §9.13 vprc setpoint conditioning ----
            Rec(0x6391, "vprc setpoint conditioning reference value", "9.13.2", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(16384), Ir, P(0)),
            Rec(0x6394, "vprc setpoint conditioning hold setpoint", "9.13.3", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir, P(0)),
            Rec(0x6398, "vprc setpoint conditioning reference value 32-bit", "9.13.4", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(100000), Ir32, P(0)),
            Rec(0x639B, "vprc setpoint conditioning hold setpoint 32-bit", "9.13.5", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir32, P(0)),
            Rec(0x63A0, "vprc setpoint conditioning upper setpoint limit", "9.13.6", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(32767), Cms, Cms),
            Rec(0x63A1, "vprc setpoint conditioning lower setpoint limit", "9.13.7", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-32768), Cms, Cms),
            Var(0x63A2, "vprc setpoint conditioning scaling factor", "9.13.8", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), V(65537)),
            Rec(0x63A3, "vprc setpoint conditioning scaling offset", "9.13.9", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), V(0), Ir, P(0)),
            Rec(0x63A8, "vprc setpoint conditioning upper limit 32-bit", "9.13.10", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x63A9, "vprc setpoint conditioning lower limit 32-bit", "9.13.11", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x63B0, "vprc setpoint conditioning ramp type", "9.13.12", ValvePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x63B1, "vprc setpoint conditioning ramp acceleration time", "9.13.13", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x63B2, "vprc setpoint conditioning ramp acceleration time positive", "9.13.14", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63B3, "vprc setpoint conditioning ramp acceleration time negative", "9.13.15", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63B4, "vprc setpoint conditioning ramp deceleration time", "9.13.16", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x63B5, "vprc setpoint conditioning ramp deceleration time positive", "9.13.17", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63B6, "vprc setpoint conditioning ramp deceleration time negative", "9.13.18", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63B8, "vprc setpoint conditioning ramp acceleration time 32-bit", "9.13.19", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x63B9, "vprc setpoint conditioning ramp acceleration time positive 32-bit", "9.13.20", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63BA, "vprc setpoint conditioning ramp acceleration time negative 32-bit", "9.13.21", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63BB, "vprc setpoint conditioning ramp deceleration time 32-bit", "9.13.22", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x63BC, "vprc setpoint conditioning ramp deceleration time positive 32-bit", "9.13.23", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x63BD, "vprc setpoint conditioning ramp deceleration time negative 32-bit", "9.13.24", ValvePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.14 vprc control monitoring (numbered 9.14.12 and on by the norm) ----
            Var(0x63D1, "vprc control monitoring type", "9.14.14", ValvePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)),
            Rec(0x63D2, "vprc control monitoring delay time", "9.14.15", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x63D3, "vprc control monitoring symmetric threshold", "9.14.16", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x63D4, "vprc control monitoring upper threshold", "9.14.17", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63D5, "vprc control monitoring lower threshold", "9.14.18", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63D8, "vprc control monitoring threshold 32-bit", "9.14.19", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms), // ruling: 9.14.19, not 9.12.10 (see the class doc)
            Rec(0x63D9, "vprc control monitoring upper threshold 32-bit", "9.14.20", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63DA, "vprc control monitoring lower threshold 32-bit", "9.14.21", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.15 vprc target window monitoring ----
            Var(0x63F0, "vprc target window monitoring type", "9.15.2", ValvePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x63F1, "vprc target window monitoring switch on time", "9.15.3", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, Ds, U(0x03), P(-3)),
            Rec(0x63F2, "vprc target window monitoring switch off time", "9.15.4", ValvePressure, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, Ds, U(0x03), P(-3)),
            Rec(0x63F3, "vprc target window monitoring symmetric threshold", "9.15.5", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x63F4, "vprc target window monitoring upper threshold", "9.15.6", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63F5, "vprc target window monitoring lower threshold", "9.15.7", ValvePressure, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63F8, "vprc target window monitoring threshold 32-bit", "9.15.8", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x63F9, "vprc target window monitoring upper threshold 32-bit", "9.15.9", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x63FA, "vprc target window monitoring lower threshold 32-bit", "9.15.10", ValvePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.16 pQ control (vpqc): process values ----
            VpqcPowerLimitFactor = Var(0x640D, "vpqc power limit factor", "9.16.2", ValvePower, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 5"), V(65537)),
            VpqcHydrostaticActualPower = Rec(0x640E, "vpqc hydrostatic actual power", "9.16.3", ValvePower, Integer16, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 5"), V(0), Ir, P(0)),

            // ---- §9.20 vpqc target window monitoring ----
            Var(0x6470, "vpqc target window monitoring type", "9.20.2", ValvePower, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x6471, "vpqc target window monitoring switch on time", "9.20.3", ValvePower, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6472, "vpqc target window monitoring switch off time", "9.20.4", ValvePower, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6473, "vpqc target window monitoring symmetric threshold", "9.20.5", ValvePower, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6474, "vpqc target window monitoring upper threshold", "9.20.6", ValvePower, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6475, "vpqc target window monitoring lower threshold", "9.20.7", ValvePower, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6478, "vpqc target window monitoring threshold 32-bit", "9.20.8", ValvePower, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6479, "vpqc target window monitoring upper threshold 32-bit", "9.20.9", ValvePower, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x647A, "vpqc target window monitoring lower threshold 32-bit", "9.20.10", ValvePower, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.21 Flow control (vflc): process values ----
            VflcSetpoint = Rec(0x6700, "vflc setpoint", "9.21.2", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 13, 14 or 15"), V(0), U(0xA9), P(-3)),
            VflcActualValue = Rec(0x6701, "vflc actual value", "9.21.3", ValveFlow, Integer16, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 13, 14 or 15"), No, U(0xA9), P(-3)),
            Var(0x6702, "vflc interface reference", "9.21.4", ValveFlow, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            VflcSetpoint32 = Rec(0x6708, "vflc setpoint 32-bit", "9.21.5", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("32-bit parameters and control mode is 13, 14 or 15"), V(0), U(0xA9), P(-3)), // Annex A: Optional
            VflcActualValue32 = Rec(0x6709, "vflc actual value 32-bit", "9.21.6", ValveFlow, Integer32, Acc.Ro, Pdo.T, If("32-bit parameters and control mode is 13, 14 or 15"), No, U(0xA9), P(-3)), // Annex A: Optional
            Rec(0x670A, "vflc setpoint conditioning demand value 32-bit", "9.21.7", ValveFlow, Integer32, Acc.Ro, Pdo.T, Optional, No, Cms, Cms), // sub-index 00h default "No"
            Rec(0x6710, "vflc setpoint conditioning demand value", "9.21.8", ValveFlow, Integer16, Acc.Ro, Pdo.T, Optional, No, Cms, Cms),
            Rec(0x6750, "vflc control deviation", "9.21.9", ValveFlow, Integer16, Acc.Ro, Pdo.T, Optional, No, U(0xA9), P(-3)),
            Rec(0x6758, "vflc control deviation 32-bit", "9.21.10", ValveFlow, Integer32, Acc.Ro, Pdo.T, Optional, No, U(0xA9), P(-3)), // Annex A: Conditional

            // ---- §9.22 vflc setpoint conditioning ----
            Rec(0x6711, "vflc setpoint conditioning reference value", "9.22.2", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(16384), Ir, P(0)),
            Rec(0x6714, "vflc setpoint conditioning hold setpoint", "9.22.3", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir, P(0)),
            Rec(0x6718, "vflc setpoint conditioning reference value 32-bit", "9.22.4", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(100000), Ir32, P(0)),
            Rec(0x671B, "vflc setpoint conditioning hold setpoint 32-bit", "9.22.5", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Ir32, P(0)),
            Rec(0x6720, "vflc setpoint conditioning upper setpoint limit", "9.22.6", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(32767), Cms, Cms),
            Rec(0x6721, "vflc setpoint conditioning lower setpoint limit", "9.22.7", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-32768), Cms, Cms),
            Var(0x6722, "vflc setpoint conditioning scaling factor", "9.22.8", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), V(65537)),
            Rec(0x6723, "vflc setpoint conditioning scaling offset", "9.22.9", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("scaling function is implemented"), V(0), Ir, P(0)),
            Rec(0x6728, "vflc setpoint conditioning upper setpoint limit 32-bit", "9.22.10", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x6729, "vflc setpoint conditioning lower setpoint limit 32-bit", "9.22.11", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x6730, "vflc setpoint conditioning ramp type", "9.22.12", ValveFlow, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x6731, "vflc setpoint conditioning ramp acceleration time", "9.22.13", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6732, "vflc setpoint conditioning ramp acceleration time positive", "9.22.14", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6733, "vflc setpoint conditioning ramp acceleration time negative", "9.22.15", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6734, "vflc setpoint conditioning ramp deceleration time", "9.22.16", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x6735, "vflc setpoint conditioning ramp deceleration time positive", "9.22.17", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6736, "vflc setpoint conditioning ramp deceleration time negative", "9.22.18", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6738, "vflc setpoint conditioning ramp acceleration time 32-bit", "9.22.19", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6739, "vflc setpoint conditioning ramp acceleration time positive 32-bit", "9.22.20", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x673A, "vflc setpoint conditioning ramp acceleration time negative 32-bit", "9.22.21", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x673B, "vflc setpoint conditioning ramp deceleration time 32-bit", "9.22.22", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x673C, "vflc setpoint conditioning ramp deceleration time positive 32-bit", "9.22.23", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x673D, "vflc setpoint conditioning ramp deceleration time negative 32-bit", "9.22.24", ValveFlow, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.24 vflc control monitoring ----
            Var(0x6751, "vflc control monitoring type", "9.24.2", ValveFlow, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)),
            Rec(0x6752, "vflc control monitoring delay time", "9.24.3", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6753, "vflc control monitoring symmetric threshold", "9.24.4", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6754, "vflc control monitoring upper threshold", "9.24.5", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6755, "vflc control monitoring lower threshold", "9.24.6", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6759, "vflc control monitoring threshold 32-bit", "9.24.7", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x675A, "vflc control monitoring upper threshold 32-bit", "9.24.8", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x675B, "vflc control monitoring lower threshold 32-bit", "9.24.9", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.25 vflc target window monitoring ----
            Var(0x6770, "vflc target window monitoring type", "9.25.2", ValveFlow, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x6771, "vflc target window monitoring switch on time", "9.25.3", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6772, "vflc target window monitoring switch off time", "9.25.4", ValveFlow, Unsigned16, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6773, "vflc target window monitoring symmetric threshold", "9.25.5", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6774, "vflc target window monitoring upper threshold", "9.25.6", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6775, "vflc target window monitoring lower threshold", "9.25.7", ValveFlow, Integer16, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6778, "vflc target window monitoring threshold 32-bit", "9.25.8", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6779, "vflc target window monitoring upper threshold 32-bit", "9.25.9", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x677A, "vflc target window monitoring lower threshold 32-bit", "9.25.10", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms), // sub-index 00h default "No"

            // ---- §9.31 pFlow control with power limitation (vpflcpl) ----
            VpflcplPowerLimit = Rec(0x6780, "vpflcpl power limit", "9.31.2", ValveFlow, Integer32, Acc.Rw, Pdo.TrROrNo, If("control mode 15 is supported"), V(0), U(0x24), P(-3)),
            VpflcplPowerActualValue = Rec(0x6781, "vpflcpl power actual value", "9.31.3", ValveFlow, Integer32, Acc.Ro, Pdo.T, If("control mode 15 is supported"), No, U(0x24), P(-3)),

            // ---- §9.32 Open loop movement control (dcol): process values ----
            DcolSetpoint = Rec(0x6480, "dcol setpoint", "9.32.2", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, If("control mode 6 is supported"), V(0), U(0xA1), P(-3)),
            Rec(0x6490, "dcol setpoint conditioning demand value", "9.32.3", DriveOpenLoop, Integer32, Acc.Ro, Pdo.T, Optional, No, Cms, Cms),
            Rec(0x6494, "dcol setpoint conditioning hold setpoint", "9.32.4", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),

            // ---- §9.33 dcol setpoint conditioning ----
            Rec(0x6492, "dcol setpoint conditioning reference A value", "9.33.2", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6493, "dcol setpoint conditioning reference B value", "9.33.3", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x64A0, "dcol setpoint conditioning upper limit", "9.33.4", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x64A1, "dcol setpoint conditioning lower limit", "9.33.5", DriveOpenLoop, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x64B0, "dcol setpoint conditioning ramp type", "9.33.6", DriveOpenLoop, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x64B1, "dcol setpoint conditioning ramp acceleration time", "9.33.7", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x64B2, "dcol setpoint conditioning ramp acceleration time positive", "9.33.8", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x64B3, "dcol setpoint conditioning ramp acceleration time negative", "9.33.9", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x64B4, "dcol setpoint conditioning ramp deceleration time", "9.33.10", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x64B5, "dcol setpoint conditioning ramp deceleration time positive", "9.33.11", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x64B6, "dcol setpoint conditioning ramp deceleration time negative", "9.33.12", DriveOpenLoop, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.37 Speed control (dsc): process values ----
            DscSetpoint = Rec(0x6500, "dsc setpoint", "9.37.2", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control mode is 7"), V(0), U(0xA1), P(-3)),
            DscActualValue = Rec(0x6501, "dsc actual value", "9.37.3", DriveVelocity, Integer32, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 7"), No, U(0xA1), P(-3)),
            Var(0x6502, "dsc interface reference", "9.37.4", DriveVelocity, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6510, "dsc setpoint conditioning demand value", "9.37.5", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6514, "dsc setpoint conditioning hold setpoint", "9.37.6", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6550, "dsc control deviation", "9.37.7", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0xA1), P(-3)),

            // ---- §9.38 dsc setpoint conditioning ----
            Rec(0x6503, "dsc Kp", "9.38.2", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 7"), V(0), U(0xA5), P(-3)),
            Rec(0x6504, "dsc Ti", "9.38.3", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 7"), V(0), U(0x03), P(-3)),
            Rec(0x6512, "dsc setpoint conditioning reference A value", "9.38.4", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6513, "dsc setpoint conditioning reference B value", "9.38.5", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6520, "dsc setpoint conditioning upper limit", "9.38.6", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x6521, "dsc setpoint conditioning lower limit", "9.38.7", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x6530, "dsc setpoint conditioning ramp type", "9.38.8", DriveVelocity, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x6531, "dsc setpoint conditioning ramp acceleration time", "9.38.9", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6532, "dsc setpoint conditioning ramp acceleration time positive", "9.38.10", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6533, "dsc setpoint conditioning ramp acceleration time negative", "9.38.11", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6534, "dsc setpoint conditioning ramp deceleration time", "9.38.12", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x6535, "dsc setpoint conditioning ramp deceleration time positive", "9.38.13", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6536, "dsc setpoint conditioning ramp deceleration time negative", "9.38.14", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.40 dsc control monitoring ----
            Var(0x6551, "dsc control monitoring type", "9.40.2", DriveVelocity, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)),
            Rec(0x6552, "dsc control monitoring delay time", "9.40.3", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6553, "dsc control monitoring threshold", "9.40.4", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6554, "dsc control monitoring upper threshold", "9.40.5", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6555, "dsc control monitoring lower threshold", "9.40.6", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6556, "dsc control monitoring threshold Vmax", "9.40.7", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 4"), Ds, Cms, Cms),
            Rec(0x6557, "dsc control monitoring upper threshold Vmax positive", "9.40.8", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),
            Rec(0x6558, "dsc control monitoring lower threshold Vmax negative", "9.40.9", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),

            // ---- §9.41 dsc target window monitoring ----
            Var(0x6570, "dsc target window monitoring type", "9.41.2", DriveVelocity, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x6571, "dsc target window monitoring switch on time", "9.41.3", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6572, "dsc target window monitoring switch off time", "9.41.4", DriveVelocity, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6573, "dsc target window monitoring threshold", "9.41.5", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6574, "dsc target window monitoring upper threshold", "9.41.6", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6575, "dsc target window monitoring lower threshold", "9.41.7", DriveVelocity, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.42 Pressure (force) control (dfpc): process values ----
            DfpcSetpoint = Rec(0x6580, "dfpc setpoint", "9.42.2", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control mode is 8"), V(0), U(0x4E), P(-3)),
            DfpcActualValue = Rec(0x6581, "dfpc actual value", "9.42.3", DrivePressure, Integer32, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 8"), No, U(0x4E), P(-3)),
            Var(0x6582, "dfpc interface reference", "9.42.4", DrivePressure, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6590, "dfpc setpoint conditioning demand value", "9.42.5", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6594, "dfpc setpoint conditioning hold setpoint", "9.42.6", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x65D0, "dfpc control deviation", "9.42.7", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x4E), P(-3)), // rw / TR, R or No / 0, unlike the other control deviations
            Rec(0x6583, "dfpc Kp", "9.42.8", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 8"), V(0), U(0xA2), P(-3)),
            Rec(0x6584, "dfpc Td", "9.42.9", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 8"), V(0), U(0x03), P(-3)),
            Rec(0x6585, "dfpc T1", "9.42.10", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 8"), V(0), U(0x03), P(-3)),
            Rec(0x6586, "dfpc Ti", "9.42.11", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 8"), V(0), U(0x03), P(-3)),
            Rec(0x6587, "dfpc pressure sample time", "9.42.12", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 8"), Ms, U(0x03), P(-6)), // default "0 (NOTE), Manufacturer-specific": 0 with the pressure controller disabled

            // ---- §9.43 dfpc setpoint conditioning ----
            Rec(0x6592, "dfpc setpoint conditioning reference A value", "9.43.2", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x4E), P(-6)),
            Rec(0x6593, "dfpc setpoint conditioning reference B value", "9.43.3", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x4E), P(-6)),
            Rec(0x65A0, "dfpc setpoint conditioning upper limit", "9.43.4", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x65A1, "dfpc setpoint conditioning lower limit", "9.43.5", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x65B0, "dfpc setpoint conditioning ramp type", "9.43.6", DrivePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x65B1, "dfpc setpoint conditioning ramp acceleration time", "9.43.7", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x65B2, "dfpc setpoint conditioning ramp acceleration time positive", "9.43.8", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x65B3, "dfpc setpoint conditioning ramp acceleration time negative", "9.43.9", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x65B4, "dfpc setpoint conditioning ramp deceleration time", "9.43.10", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x65B5, "dfpc setpoint conditioning ramp deceleration time positive", "9.43.11", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x65B6, "dfpc setpoint conditioning ramp deceleration time negative", "9.43.12", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),

            // ---- §9.45 dfpc control monitoring ----
            Var(0x65D1, "dfpc control monitoring type", "9.45.2", DrivePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)), // ruling: the heading; the object table says 63D1h
            Rec(0x65D2, "dfpc control monitoring delay time", "9.45.3", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x65D3, "dfpc control monitoring threshold", "9.45.4", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x65D4, "dfpc control monitoring upper threshold", "9.45.5", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x65D5, "dfpc control monitoring lower threshold", "9.45.6", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x65D6, "dfpc control monitoring threshold Vmax", "9.45.7", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 4"), Ds, Cms, Cms),
            Rec(0x65D7, "dfpc control monitoring upper threshold Vmax positive", "9.45.8", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),
            Rec(0x65D8, "dfpc control monitoring lower threshold Vmax negative", "9.45.9", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),

            // ---- §9.46 dfpc target window monitoring ----
            Var(0x65F0, "dfpc target window monitoring type", "9.46.2", DrivePressure, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x65F1, "dfpc target window monitoring switch on time", "9.46.3", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x65F2, "dfpc target window monitoring switch off time", "9.46.4", DrivePressure, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x65F3, "dfpc target window monitoring threshold", "9.46.5", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x65F4, "dfpc target window monitoring upper threshold", "9.46.6", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x65F5, "dfpc target window monitoring lower threshold", "9.46.7", DrivePressure, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),

            // ---- §9.47 Position control (dpc): process values ----
            DpcSetpoint = Rec(0x6600, "dpc setpoint", "9.47.2", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control mode is 9"), V(0), U(0x01), P(-6)),
            DpcActualValue = Rec(0x6601, "dpc actual value", "9.47.3", DrivePosition, Integer32, Acc.Ro, Pdo.T, If("16-bit parameters and control mode is 9"), No, U(0x01), P(-6)),
            Var(0x6602, "dpc interface reference", "9.47.4", DrivePosition, Unsigned8, Acc.Rw, Pdo.TrROrNo, Optional, V(1)),
            Rec(0x6603, "dpc Kp", "9.47.5", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 9"), V(0), U(0xA3), P(-3)),
            Rec(0x6604, "dpc Td", "9.47.6", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 9"), V(0), U(0x03), P(-3)),
            Rec(0x6605, "dpc T1", "9.47.7", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("16-bit parameters and control mode is 9"), V(0), U(0x03), P(-3)), // ruling: the heading; the object table says 6604h
            Var(0x6608, "dpc switched integrator type", "9.47.8", DrivePosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("switched integrator is implemented"), V(0)),
            Rec(0x6609, "dpc switched integrator Ti", "9.47.9", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("switched integrator type is 1"), V(0), U(0x03), P(-3)),
            Rec(0x660A, "dpc switched integrator dX", "9.47.10", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("switched integrator type is 1"), V(0), U(0x01), P(-6)),
            Rec(0x660C, "dpc condition feedback Kv", "9.47.11", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("condition feedback function is implemented"), V(0), U(0xA5), P(-3)),
            Rec(0x660D, "dpc condition feedback Ka", "9.47.12", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("condition feedback function is implemented"), V(0), U(0xA6), P(-3)),
            Rec(0x660E, "dpc condition feedback Kpp", "9.47.13", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("condition feedback function is implemented"), V(0), U(0xA2), P(-3)),
            Rec(0x660F, "dpc condition feedback T1pp", "9.47.14", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("condition feedback function is implemented"), V(0), U(0x03), P(-3)),
            Rec(0x6610, "dpc setpoint conditioning demand value", "9.47.15", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6614, "dpc setpoint conditioning hold setpoint", "9.47.16", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms), // name from the heading; the object description says "dfpc …"
            Rec(0x6650, "dpc control deviation", "9.47.17", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x01), P(-6)), // rw / TR, R or No / 0, unlike the other control deviations

            // ---- §9.48 dpc setpoint conditioning ----
            Rec(0x6612, "dpc setpoint conditioning reference A value", "9.48.2", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6613, "dpc setpoint conditioning reference B value", "9.48.3", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), Cms, Cms),
            Rec(0x6620, "dpc setpoint conditioning upper limit", "9.48.4", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(2147483647), Cms, Cms),
            Rec(0x6621, "dpc setpoint conditioning lower limit", "9.48.5", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("limit function is implemented"), V(-2147483648), Cms, Cms),
            Var(0x6630, "dpc setpoint conditioning ramp type", "9.48.6", DrivePosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("ramp function is implemented"), V(0)),
            Rec(0x6631, "dpc setpoint conditioning ramp acceleration time", "9.48.7", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 1, 2 or 4"), V(0), U(0x03), P(-3)),
            Rec(0x6632, "dpc setpoint conditioning ramp acceleration time positive", "9.48.8", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6633, "dpc setpoint conditioning ramp acceleration time negative", "9.48.9", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6634, "dpc setpoint conditioning ramp deceleration time", "9.48.10", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 2"), V(0), U(0x03), P(-3)),
            Rec(0x6635, "dpc setpoint conditioning ramp deceleration time positive", "9.48.11", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6636, "dpc setpoint conditioning ramp deceleration time negative", "9.48.12", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 3"), V(0), U(0x03), P(-3)),
            Rec(0x6637, "dpc setpoint conditioning ramp velocity", "9.48.13", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 5 or 6"), V(0), U(0xA1), P(-3)),
            Rec(0x6638, "dpc setpoint conditioning ramp acceleration", "9.48.14", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 5 or 6"), V(0), U(0xA8), P(-3)),
            Rec(0x6639, "dpc setpoint conditioning ramp deceleration", "9.48.15", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, If("ramp type is 5 or 6"), V(0), U(0xA8), P(-3)),

            // ---- §9.50 dpc control monitoring ----
            Var(0x6651, "dpc control monitoring type", "9.50.2", DrivePosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("control monitoring is implemented"), V(0)),
            Rec(0x6652, "dpc control monitoring delay time", "9.50.3", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6653, "dpc control monitoring threshold", "9.50.4", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6654, "dpc control monitoring upper threshold", "9.50.5", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6655, "dpc control monitoring lower threshold", "9.50.6", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6656, "dpc control monitoring threshold Vmax", "9.50.7", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 4"), Ds, Cms, Cms),
            Rec(0x6657, "dpc control monitoring upper threshold Vmax positive", "9.50.8", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),
            Rec(0x6658, "dpc control monitoring lower threshold Vmax negative", "9.50.9", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("control monitoring type is 3"), Ds, Cms, Cms),

            // ---- §9.51 dpc target window monitoring ----
            Var(0x6670, "dpc target window monitoring type", "9.51.2", DrivePosition, Integer8, Acc.Rw, Pdo.TrROrNo, If("target window monitoring is implemented"), V(0)),
            Rec(0x6671, "dpc target window monitoring switch on time", "9.51.3", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6672, "dpc target window monitoring switch off time", "9.51.4", DrivePosition, Unsigned32, Acc.Rw, Pdo.TrROrNo, Optional, V(0), U(0x03), P(-3)),
            Rec(0x6673, "dpc target window monitoring threshold", "9.51.5", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 2"), Ds, Cms, Cms),
            Rec(0x6674, "dpc target window monitoring upper threshold", "9.51.6", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
            Rec(0x6675, "dpc target window monitoring lower threshold", "9.51.7", DrivePosition, Integer32, Acc.Rw, Pdo.TrROrNo, If("target window monitoring type is 1"), Ds, Cms, Cms),
        };

        All = Array.AsReadOnly(table.OrderBy(d => d.Index).ToArray());
        ByIndex = Index(table);
        Reserved = Array.AsReadOnly(new ushort[] { 0x605F, 0x6280, 0x63A4, 0x63C0, 0x63C1, 0x63C2, 0x63C3, 0x63C4, 0x63C5, 0x63C6, 0x63E0, 0x63E1, 0x63E2, 0x6460, 0x6461, 0x6462 });
    }
}
