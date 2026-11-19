using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The control modes of object <c>6043h</c> (CiA 408 v2.2.0 §9.3.5, Table 82): which controller the
/// device runs. The values are those the object carries. 1 to 5 and 13 to 15 are valve (or pump)
/// modes, 6 to 12 drive modes. Table 82 also has 0 "not defined", 16 to 127 reserved and negative
/// values for the manufacturer. None of those is a member, and <see cref="Cia408Configuration.Validate"/>
/// refuses them.
/// </summary>
internal enum Cia408ControlMode : sbyte
{
    /// <summary>1, Table 82 "Q control open loop": spool position open loop (§8.5.1 calls it valve
    /// position control open loop).</summary>
    SpoolPositionOpenLoop = 1,

    /// <summary>2, Table 82 "Q control closed loop": spool position closed loop.</summary>
    SpoolPositionClosedLoop = 2,

    /// <summary>3, Table 82 "p control valve open loop": pressure control of a valve, open loop.</summary>
    PressureOpenLoop = 3,

    /// <summary>4, Table 82 "p control valve closed loop": pressure control of a valve, closed loop.</summary>
    PressureClosedLoop = 4,

    /// <summary>5, Table 82 "pQ control": pressure and flow.</summary>
    PQControl = 5,

    /// <summary>6, Table 82 "Open loop movement" (drive).</summary>
    OpenLoopMovement = 6,

    /// <summary>7, Table 82 "Velocity control axis" (drive).</summary>
    VelocityControlAxis = 7,

    /// <summary>8, Table 82 "Pressure control axis" (drive; Table 53 calls it pressure (force)
    /// control).</summary>
    PressureControlAxis = 8,

    /// <summary>9, Table 82 "Position control axis" (drive).</summary>
    PositionControlAxis = 9,

    /// <summary>10, Table 82 "Positional dependent deceleration" (drive).</summary>
    PositionalDependentDeceleration = 10,

    /// <summary>11, Table 82 "Position control axis with pressure (force) override" (drive).</summary>
    PositionControlAxisWithPressureOverride = 11,

    /// <summary>12, Table 82 "Velocity control axis with pressure (force) override" (drive).</summary>
    VelocityControlAxisWithPressureOverride = 12,

    /// <summary>13, Table 82 "Flow control".</summary>
    FlowControl = 13,

    /// <summary>14, Table 82 "pFlow control".</summary>
    PFlowControl = 14,

    /// <summary>15, Table 82 "pFlow control with power limitation".</summary>
    PFlowWithPowerLimitation = 15,
}

/// <summary>
/// The device modes of object <c>6042h</c> (CiA 408 v2.2.0 §9.3.4, Table 79): where the setpoint comes
/// from. The values are those the object carries. 7 to 127 are reserved and negative values belong
/// to the manufacturer. Neither is a member, and <see cref="Cia408Configuration.Validate"/> refuses them.
/// </summary>
/// <remarks>Table 79 gives 3 to 6 for drives only: its valves column marks them "n.a.". So
/// <see cref="Cia408Configuration.Validate"/> refuses any of them in a configuration whose
/// control modes include no drive mode (6 to 12). This applies whatever the resolution. Nothing
/// derived here depends on the device modes.</remarks>
internal enum Cia408DeviceMode : sbyte
{
    /// <summary>0, "No device mode".</summary>
    NoDeviceMode = 0,

    /// <summary>1: the setpoint comes in remotely over the bus. This is the default of <c>6042h</c>
    /// when the object is writable (§9.3.4).</summary>
    SetpointViaBus = 1,

    /// <summary>2: the setpoint comes in locally.</summary>
    SetpointLocal = 2,

    /// <summary>3, drives only: install mode. Table 79's note marks it single step.</summary>
    InstallMode = 3,

    /// <summary>4, drives only: reference mode.</summary>
    ReferenceMode = 4,

    /// <summary>5, drives only: automatic.</summary>
    Automatic = 5,

    /// <summary>6, drives only: automatic, marked single step by Table 79's note.</summary>
    AutomaticSingleStep = 6,
}

/// <summary>
/// The device type and parameter resolution, field dtprs of the device-type word (CiA 408 v2.2.0
/// §8.6.2, Table 53, bits 29 and 30). Each value is the field's code. Code 00b is reserved and so is
/// not a member, which leaves the enum's default invalid: a device has to say what it is.
/// </summary>
/// <remarks>The resolution applies to the control word, the status word and the related parameters
/// (Table 53, NOTE 2). This decides which of the two pairs of words a device uses.</remarks>
internal enum Cia408Resolution
{
    /// <summary>01b: a valve (or pump) with 16-bit parameters. Drives are not supported.</summary>
    Valve16 = 1,

    /// <summary>10b: a valve (or pump) with 32-bit parameters and a drive with 32-bit parameters.</summary>
    Valve32Drive32 = 2,

    /// <summary>11b: a valve (or pump) with 16-bit parameters and a drive with 32-bit parameters.</summary>
    Valve16Drive32 = 3,
}

/// <summary>One default PDO of a CiA 408 device (CiA 408 v2.2.0 §8.4, Table 13; §8.5, Table 32):
/// which PDO, in which direction, and what it maps. Every default PDO is valid.</summary>
/// <param name="Number">The absolute PDO number, already shifted by <c>n·64</c> for logical device
/// <c>n</c> (CiA 301 v4.2.0 §7.2.2.1).</param>
/// <param name="IsTpdo"><see langword="true"/> for a transmit PDO, <see langword="false"/> for a
/// receive PDO.</param>
/// <param name="Entries">The mapped entries in mapping order. Each is an absolute object index
/// (already shifted by <c>n·800h</c> for logical device <c>n</c>, CiA 301 v4.2.0 §7.4.1), a sub-index
/// and a length in bits. The view is read-only, so a caller cannot change a default. It compares by
/// reference, as a list does, so compare its elements.</param>
internal readonly record struct Cia408DefaultPdo(
    int Number,
    bool IsTpdo,
    IReadOnlyList<(ushort Index, byte Subindex, byte Bits)> Entries);

/// <summary>
/// What one CiA 408 device is: its parameter resolution, the control modes and device modes it
/// supports, and the logical device it occupies. From these it derives, as pure computation with no
/// node involved, the device-type word of object <c>1000h</c> (CiA 408 v2.2.0 §8.6.2) and the
/// default PDOs (§8.4, §8.5).
/// </summary>
/// <remarks>
/// <para>The first entry of <see cref="ControlModes"/> is the default of <c>6043h</c>, and it decides
/// the default PDOs.</para>
/// <para><see cref="DeviceType"/>, <see cref="DefaultPdos"/> and <see cref="RequiredPdoCount"/>
/// call <see cref="Validate"/> themselves. A configuration that no device could have answers
/// nothing: it throws what <see cref="Validate"/> throws. The properties can be set only while the
/// object is created, but the object is not frozen against a caller who keeps a reference to a list
/// and changes it before reading. Every read computes from the values at that moment.</para>
/// </remarks>
internal sealed class Cia408Configuration
{
    /// <summary>The first bit of the dtprs field of the device-type word (Table 53).</summary>
    private const int ResolutionShift = 29;

    /// <summary>The PDO that carries mapping 1 and the one that carries the mapping of the default
    /// control mode (see <see cref="DefaultPdos"/>).</summary>
    private const int WordPdo = 1;
    private const int ModePdo = 2;

    /// <summary>The resolution, field dtprs (Table 53).</summary>
    public Cia408Resolution Resolution { get; init; }

    /// <summary>The supported control modes. The first is the default of <c>6043h</c>, and it
    /// decides the default PDOs. None of them may appear twice. Empty by default, which
    /// <see cref="Validate"/> refuses: a device runs at least one controller.</summary>
    public IReadOnlyList<Cia408ControlMode> ControlModes { get; init; } = Array.Empty<Cia408ControlMode>();

    /// <summary>The supported device modes (<c>6042h</c>). None of them may appear twice. Modes 3 to
    /// 6 need a drive control mode in <see cref="ControlModes"/> (see <see cref="Cia408DeviceMode"/>). By
    /// default only <see cref="Cia408DeviceMode.SetpointViaBus"/>, the default of <c>6042h</c>
    /// (§9.3.4).</summary>
    public IReadOnlyList<Cia408DeviceMode> DeviceModes { get; init; } = new[] { Cia408DeviceMode.SetpointViaBus };

    /// <summary>The logical device the objects and PDOs belong to, <c>0</c> to <c>7</c>. Indices are
    /// <c>n·800h</c> higher and PDO numbers <c>n·64</c> higher (CiA 301 v4.2.0 §7.2.2.1,
    /// §7.4.1).</summary>
    public int LogicalDevice { get; init; }

    /// <summary>The default of <see cref="ProcessInterval"/>: 10 ms.</summary>
    public static TimeSpan DefaultProcessInterval { get; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How often the device steps its process model (<see cref="ICia408Process"/>) on the
    /// node's clock; positive. <see cref="DefaultProcessInterval"/> unless set. The norm gives no
    /// cycle time; the value is the application's, and 10 ms is a choice that keeps a simulated
    /// valve responsive without loading the node's actor loop.</summary>
    public TimeSpan ProcessInterval { get; init; } = DefaultProcessInterval;

    /// <summary>Checks that the configuration describes a device the norm allows.</summary>
    /// <exception cref="ArgumentNullException"><see cref="ControlModes"/> or
    /// <see cref="DeviceModes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not one of Table 53, a control
    /// mode is not one of Table 82, a device mode is not one of Table 79, or the logical device lies
    /// outside <c>0</c> to <c>7</c>, or <see cref="ProcessInterval"/> is not positive. The parameter
    /// name is that of the property.</exception>
    /// <exception cref="ArgumentException">There is no control mode or no device mode, a mode appears
    /// twice, a drive control mode is given with <see cref="Cia408Resolution.Valve16"/>, whose
    /// dtprs code 01b says the device supports no drive (Table 53), or a device mode of 3 to 6 is
    /// given without a drive control mode (§9.3.4, Table 79: "n.a." for valves).</exception>
    public void Validate()
    {
        if (Resolution is not (Cia408Resolution.Valve16 or Cia408Resolution.Valve32Drive32 or Cia408Resolution.Valve16Drive32))
        {
            throw new ArgumentOutOfRangeException(nameof(Resolution), Resolution,
                $"Resolution is {(int)Resolution}: it is one of the dtprs codes 01b, 10b and 11b (CiA 408 §8.6.2, Table 53).");
        }

        if (LogicalDevice is < 0 or >= CanOpenLogicalDeviceAddressing.MaxLogicalDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(LogicalDevice), LogicalDevice,
                $"LogicalDevice is {LogicalDevice}: it lies in 0 to {CanOpenLogicalDeviceAddressing.MaxLogicalDevices - 1}.");
        }

        if (ProcessInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessInterval), ProcessInterval,
                $"ProcessInterval is {ProcessInterval}: the process model steps at a positive interval.");
        }

        if (ControlModes is null)
        {
            throw new ArgumentNullException(nameof(ControlModes));
        }

        if (DeviceModes is null)
        {
            throw new ArgumentNullException(nameof(DeviceModes));
        }

        if (ControlModes.Count == 0)
        {
            throw new ArgumentException(
                "A CiA 408 device supports at least one control mode (CiA 408 §9.3.5): ControlModes is empty.",
                nameof(ControlModes));
        }

        var controlModes = new HashSet<Cia408ControlMode>();
        var hasDriveMode = false;
        foreach (var mode in ControlModes)
        {
            if (mode is < Cia408ControlMode.SpoolPositionOpenLoop or > Cia408ControlMode.PFlowWithPowerLimitation)
            {
                throw new ArgumentOutOfRangeException(nameof(ControlModes), mode,
                    $"Control mode {(int)mode} is not one of the values 1 to 15 of CiA 408 §9.3.5, Table 82.");
            }

            if (!controlModes.Add(mode))
            {
                throw new ArgumentException($"ControlModes names {mode} more than once.", nameof(ControlModes));
            }

            if (IsDriveMode(mode) && Resolution == Cia408Resolution.Valve16)
            {
                throw new ArgumentException(
                    $"{mode} is a drive control mode, and a device with resolution {nameof(Cia408Resolution.Valve16)} "
                    + "supports no drive (dtprs 01b, CiA 408 §8.6.2, Table 53).",
                    nameof(ControlModes));
            }

            hasDriveMode |= IsDriveMode(mode);
        }

        if (DeviceModes.Count == 0)
        {
            throw new ArgumentException(
                "A CiA 408 device supports at least one device mode (CiA 408 §9.3.4): DeviceModes is empty.",
                nameof(DeviceModes));
        }

        var deviceModes = new HashSet<Cia408DeviceMode>();
        foreach (var mode in DeviceModes)
        {
            if (mode is < Cia408DeviceMode.NoDeviceMode or > Cia408DeviceMode.AutomaticSingleStep)
            {
                throw new ArgumentOutOfRangeException(nameof(DeviceModes), mode,
                    $"Device mode {(int)mode} is not one of the values 0 to 6 of CiA 408 §9.3.4, Table 79.");
            }

            if (!deviceModes.Add(mode))
            {
                throw new ArgumentException($"DeviceModes names {mode} more than once.", nameof(DeviceModes));
            }

            if (mode >= Cia408DeviceMode.InstallMode && !hasDriveMode)
            {
                throw new ArgumentException(
                    $"{mode} is a device mode of drives only, and ControlModes names no drive control mode "
                    + "(6 to 12): CiA 408 v2.2.0 §9.3.4, Table 79 marks device modes 3 to 6 \"n.a.\" for valves.",
                    nameof(DeviceModes));
            }
        }
    }

    /// <summary>
    /// The device-type word of object <c>1000h</c> (CiA 408 v2.2.0 §8.6.2, Figure 3, Table 53). It
    /// is built from three parts. Bits 0 to 15 hold profile <see cref="Cia408Profile.Number"/>.
    /// Bits 16 to 28 are set for each supported control mode. Bits 29 and 30 hold the
    /// <see cref="Resolution"/>. Bit 31 is reserved and stays clear. Calls <see cref="Validate"/>.
    /// </summary>
    /// <remarks>
    /// <para>Table 53 names a capability for each bit, but it gives no mapping from the values of
    /// <c>6043h</c> to bits. The pairing here is <b>inferred</b> from the names:
    /// <list type="bullet">
    /// <item><description>Bits 16 to 22 (dcm) are the drive modes 6 to 12, in order.</description></item>
    /// <item><description>Bit 23, spool position control, covers modes 1 and 2.</description></item>
    /// <item><description>Bit 24, pressure (force) control, covers modes 3 and 4.</description></item>
    /// <item><description>Bits 25 to 28 are pQ (5), flow (13), pFlow (14) and pFlow with power
    /// limitation (15).</description></item>
    /// </list></para>
    /// <para>With dtprs 01b the drive bits are don't-care (Table 53, NOTE 1). Here they are clear,
    /// because <see cref="Validate"/> refuses a drive mode on such a device. Because bit 31 is clear,
    /// the word is never <c>FFFF0198h</c>, the value Table 55 reserves for a module of several
    /// logical devices.</para>
    /// <para>This is the word of this logical device itself. In a module with several logical
    /// devices, <c>1000h</c> holds the reserved value instead, and this word moves to
    /// <c>67FFh</c> + <c>n·800h</c> (CiA 301 v4.2.0 §7.5.2.1). The node composes that. The
    /// configuration does not, and its value is the same in every logical device.</para>
    /// </remarks>
    public CanOpenDeviceType DeviceType
    {
        get
        {
            Validate();
            var value = (uint)Cia408Profile.Number | ((uint)Resolution << ResolutionShift);
            foreach (var mode in ControlModes)
            {
                value |= CapabilityBit(mode);
            }

            return new CanOpenDeviceType(value);
        }
    }

    /// <summary>
    /// The default PDOs (CiA 408 v2.2.0 §8.4, Table 13; §8.5, Table 32), ordered by number, with the
    /// receive PDO before the transmit PDO of the same number. Calls <see cref="Validate"/>.
    /// </summary>
    /// <remarks>
    /// <para>The norm gives mappings, not PDOs. It leaves the assignment of mapping <c>n</c> to a PDO
    /// number to the manufacturer (§8.4.1, §8.5.1). The layout here is the plan's decision.
    /// PDO 1 carries mapping 1: the control word in the RPDO and the status word in the TPDO.
    /// PDO 2 carries the mapping of the default control mode, if the table has a row for that mode
    /// in the width in use. Where only one table has such a row, only that direction gets a PDO 2.
    /// Every mapping row begins with the control or status word again, as the tables write it.</para>
    /// <para>Which width is in use depends on the default control mode and the resolution (Table 53,
    /// NOTE 2). A valve mode runs in 16 bit with <see cref="Cia408Resolution.Valve16"/> or
    /// <see cref="Cia408Resolution.Valve16Drive32"/>, and the device then uses <c>6040h</c> and
    /// <c>6041h</c>. A valve mode with <see cref="Cia408Resolution.Valve32Drive32"/>, and every drive
    /// mode, runs in 32 bit with <c>6044h</c> and <c>6045h</c>.</para>
    /// <para>The mapping numbers of the tables that PDO 2 carries, by default control mode:
    /// <list type="bullet">
    /// <item><description>1: RPDO mapping 2 only, 16 and 32 bit.</description></item>
    /// <item><description>2: TPDO mapping 2 and RPDO mapping 2, 16 and 32 bit.</description></item>
    /// <item><description>3: RPDO mapping 3 only, 16 and 32 bit.</description></item>
    /// <item><description>4: TPDO mapping 3 and RPDO mapping 3, 16 and 32 bit.</description></item>
    /// <item><description>5: TPDO mapping 4 and RPDO mapping 4, 16 bit only.</description></item>
    /// <item><description>6: RPDO mapping 5 only.</description></item>
    /// <item><description>7: TPDO mapping 5 and RPDO mapping 6.</description></item>
    /// <item><description>8: TPDO mapping 6 and RPDO mapping 7.</description></item>
    /// <item><description>9 and 10: TPDO mapping 7 and RPDO mapping 8.</description></item>
    /// <item><description>13: TPDO mapping 8 and RPDO mapping 9, 16 and 32 bit.</description></item>
    /// <item><description>14: TPDO mapping 9 and RPDO mapping 10, 16 bit only.</description></item>
    /// <item><description>11, 12 and 15: none.</description></item>
    /// </list>
    /// The drive mappings exist in 32 bit only.</para>
    /// <para>The norm contradicts itself on one row. Table 13 has TPDO mapping 9 (pFlow) in 16 bit
    /// only. The entry description of that mapping (§8.4.10, Table 31) gives its first object as
    /// "6045h in case of 32-bit" and the other two as 16-bit "in case of 16-bit or 32-bit value". The
    /// 32-bit variant that Table 31 implies would map <c>6045h:00</c> (32 bits), then
    /// <c>6701h:01</c> (16 bits), then <c>6381h:01</c> (16 bits). The reading taken is Table 13's,
    /// so pFlow on a 32-bit valve has no TPDO 2. Table 32's RPDO mapping 10 and its entry
    /// description (§8.5.11, Table 52) agree on 16 bit only.</para>
    /// <para><see cref="Cia408DefaultPdo"/> does not carry communication parameters. The norm defines
    /// no COB-IDs and no enable bits for these PDOs. Its one transmission rule is that a TPDO of
    /// transmission type 255 is sent immediately after the corresponding RPDO is received (§8.4.1).
    /// The plan decides what installs the default PDOs: CiA 301's pre-defined COB-IDs, each PDO
    /// valid, and every TPDO of transmission type 255.</para>
    /// </remarks>
    public IReadOnlyList<Cia408DefaultPdo> DefaultPdos
    {
        get
        {
            Validate();
            var mode = ControlModes[0];
            var wide = IsWide(mode);
            var controlWord = wide ? Cia408Objects.DeviceControlWord32 : Cia408Objects.DeviceControlWord16;
            var statusWord = wide ? Cia408Objects.DeviceStatusWord32 : Cia408Objects.DeviceStatusWord16;

            var pdos = new List<Cia408DefaultPdo>(4)
            {
                Pdo(WordPdo, isTpdo: false, controlWord),
                Pdo(WordPdo, isTpdo: true, statusWord),
            };

            if (RpdoValues(mode, wide) is { } setpoints)
            {
                pdos.Add(Pdo(ModePdo, isTpdo: false, controlWord, setpoints));
            }

            if (TpdoValues(mode, wide) is { } actualValues)
            {
                pdos.Add(Pdo(ModePdo, isTpdo: true, statusWord, actualValues));
            }

            return pdos.AsReadOnly();
        }
    }

    /// <summary>The highest PDO number, of either direction, that has a default: what the node has
    /// to provide for the default PDOs to be written. It is absolute, so it includes the
    /// <c>n·64</c> of a logical device. Calls <see cref="Validate"/>.</summary>
    public int RequiredPdoCount
    {
        get
        {
            var highest = 0;
            foreach (var pdo in DefaultPdos)
            {
                highest = Math.Max(highest, pdo.Number);
            }

            return highest;
        }
    }

    /// <summary>The setpoint records that follow the control word in the RPDO mapping of
    /// <paramref name="mode"/> (Table 32), in the width the device uses for that mode (see
    /// <see cref="DefaultPdos"/>); empty where the table has no row for it. What the device declares
    /// as the setpoints of a supported control mode.</summary>
    internal IReadOnlyList<Cia408ObjectDefinition> SetpointsOf(Cia408ControlMode mode)
        => RpdoValues(mode, IsWide(mode)) ?? Array.Empty<Cia408ObjectDefinition>();

    /// <summary>The actual value records that follow the status word in the TPDO mapping of
    /// <paramref name="mode"/> (Table 13), in the width the device uses for that mode; empty where the
    /// table has no row for it.</summary>
    internal IReadOnlyList<Cia408ObjectDefinition> ActualValuesOf(Cia408ControlMode mode)
        => TpdoValues(mode, IsWide(mode)) ?? Array.Empty<Cia408ObjectDefinition>();

    /// <summary>Whether <paramref name="mode"/> runs in 32 bit (Table 53, NOTE 2): every drive mode,
    /// and a valve mode on <see cref="Cia408Resolution.Valve32Drive32"/>.</summary>
    private bool IsWide(Cia408ControlMode mode)
        => !(IsValveMode(mode) && Resolution is Cia408Resolution.Valve16 or Cia408Resolution.Valve16Drive32);

    /// <summary>Whether <paramref name="mode"/> is a drive mode, 6 to 12 of Table 82; the others are
    /// valve (or pump) modes.</summary>
    internal static bool IsDriveMode(Cia408ControlMode mode)
        => mode is >= Cia408ControlMode.OpenLoopMovement and <= Cia408ControlMode.VelocityControlAxisWithPressureOverride;

    private static bool IsValveMode(Cia408ControlMode mode) => !IsDriveMode(mode);

    /// <summary>The control modes whose capability bit <paramref name="deviceType"/> sets, ascending:
    /// the reverse of <see cref="DeviceType"/>, with the same inferred pairing. Bit 23 names modes 1
    /// and 2 and bit 24 modes 3 and 4, so each gives both: the word cannot tell an open-loop device
    /// from a closed-loop one. The profile number is not checked.</summary>
    internal static IReadOnlyList<Cia408ControlMode> ControlModesOf(CanOpenDeviceType deviceType)
    {
        var modes = new List<Cia408ControlMode>();
        for (var mode = Cia408ControlMode.SpoolPositionOpenLoop; mode <= Cia408ControlMode.PFlowWithPowerLimitation; mode++)
        {
            if ((deviceType.Value & CapabilityBit(mode)) != 0) modes.Add(mode);
        }

        return modes.AsReadOnly();
    }

    /// <summary>The resolution field dtprs of <paramref name="deviceType"/> (Table 53, bits 29 and
    /// 30), or <see langword="null"/> for the reserved code 00b.</summary>
    internal static Cia408Resolution? ResolutionOf(CanOpenDeviceType deviceType)
    {
        var code = (deviceType.Value >> ResolutionShift) & 0b11;
        return code == 0 ? null : (Cia408Resolution)code;
    }

    /// <summary>The capability bit of a control mode in the device-type word. The pairing is
    /// inferred (see <see cref="DeviceType"/>).</summary>
    private static uint CapabilityBit(Cia408ControlMode mode) => mode switch
    {
        Cia408ControlMode.OpenLoopMovement => 1u << 16,
        Cia408ControlMode.VelocityControlAxis => 1u << 17,
        Cia408ControlMode.PressureControlAxis => 1u << 18,
        Cia408ControlMode.PositionControlAxis => 1u << 19,
        Cia408ControlMode.PositionalDependentDeceleration => 1u << 20,
        Cia408ControlMode.PositionControlAxisWithPressureOverride => 1u << 21,
        Cia408ControlMode.VelocityControlAxisWithPressureOverride => 1u << 22,
        Cia408ControlMode.SpoolPositionOpenLoop or Cia408ControlMode.SpoolPositionClosedLoop => 1u << 23,
        Cia408ControlMode.PressureOpenLoop or Cia408ControlMode.PressureClosedLoop => 1u << 24,
        Cia408ControlMode.PQControl => 1u << 25,
        Cia408ControlMode.FlowControl => 1u << 26,
        Cia408ControlMode.PFlowControl => 1u << 27,
        Cia408ControlMode.PFlowWithPowerLimitation => 1u << 28,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>The setpoints that follow the control word in the RPDO mapping of
    /// <paramref name="mode"/> (Table 32, mappings 2 to 10), or <see langword="null"/> where the
    /// table has no row for the mode in that width.</summary>
    private static Cia408ObjectDefinition[]? RpdoValues(Cia408ControlMode mode, bool wide) => (mode, wide) switch
    {
        (Cia408ControlMode.SpoolPositionOpenLoop or Cia408ControlMode.SpoolPositionClosedLoop, false) => [Cia408Objects.QSetpoint],
        (Cia408ControlMode.SpoolPositionOpenLoop or Cia408ControlMode.SpoolPositionClosedLoop, true) => [Cia408Objects.QSetpoint32],
        (Cia408ControlMode.PressureOpenLoop or Cia408ControlMode.PressureClosedLoop, false) => [Cia408Objects.VprcSetpoint],
        (Cia408ControlMode.PressureOpenLoop or Cia408ControlMode.PressureClosedLoop, true) => [Cia408Objects.VprcSetpoint32],
        (Cia408ControlMode.PQControl, false) => [Cia408Objects.QSetpoint, Cia408Objects.VprcSetpoint],
        (Cia408ControlMode.OpenLoopMovement, true) => [Cia408Objects.DcolSetpoint],
        (Cia408ControlMode.VelocityControlAxis, true) => [Cia408Objects.DscSetpoint],
        (Cia408ControlMode.PressureControlAxis, true) => [Cia408Objects.DfpcSetpoint],
        (Cia408ControlMode.PositionControlAxis or Cia408ControlMode.PositionalDependentDeceleration, true) => [Cia408Objects.DpcSetpoint],
        (Cia408ControlMode.FlowControl, false) => [Cia408Objects.VflcSetpoint],
        (Cia408ControlMode.FlowControl, true) => [Cia408Objects.VflcSetpoint32],
        (Cia408ControlMode.PFlowControl, false) => [Cia408Objects.VflcSetpoint, Cia408Objects.VprcSetpoint],
        _ => null,
    };

    /// <summary>The actual values that follow the status word in the TPDO mapping of
    /// <paramref name="mode"/> (Table 13, mappings 2 to 9), or <see langword="null"/> where the
    /// table has no row for the mode in that width.</summary>
    private static Cia408ObjectDefinition[]? TpdoValues(Cia408ControlMode mode, bool wide) => (mode, wide) switch
    {
        (Cia408ControlMode.SpoolPositionClosedLoop, false) => [Cia408Objects.SpoolPositionActualValue],
        (Cia408ControlMode.SpoolPositionClosedLoop, true) => [Cia408Objects.SpoolPositionActualValue32],
        (Cia408ControlMode.PressureClosedLoop, false) => [Cia408Objects.VprcActualValue],
        (Cia408ControlMode.PressureClosedLoop, true) => [Cia408Objects.VprcActualValue32],
        (Cia408ControlMode.PQControl, false) => [Cia408Objects.SpoolPositionActualValue, Cia408Objects.VprcActualValue],
        (Cia408ControlMode.VelocityControlAxis, true) => [Cia408Objects.DscActualValue],
        (Cia408ControlMode.PressureControlAxis, true) => [Cia408Objects.DfpcActualValue],
        (Cia408ControlMode.PositionControlAxis or Cia408ControlMode.PositionalDependentDeceleration, true) => [Cia408Objects.DpcActualValue],
        (Cia408ControlMode.FlowControl, false) => [Cia408Objects.VflcActualValue],
        (Cia408ControlMode.FlowControl, true) => [Cia408Objects.VflcActualValue32],
        (Cia408ControlMode.PFlowControl, false) => [Cia408Objects.VflcActualValue, Cia408Objects.VprcActualValue],
        _ => null,
    };

    /// <summary>A default PDO of this logical device that maps <paramref name="word"/> and then
    /// <paramref name="values"/>. Sub-index, length and index come from the catalogue, and the
    /// index is shifted for the logical device.</summary>
    private Cia408DefaultPdo Pdo(int pdoInDevice0, bool isTpdo, Cia408ObjectDefinition word, Cia408ObjectDefinition[]? values = null)
    {
        var entries = new List<(ushort Index, byte Subindex, byte Bits)>(1 + (values?.Length ?? 0)) { Entry(word) };
        if (values is not null)
        {
            foreach (var value in values)
            {
                entries.Add(Entry(value));
            }
        }

        return new Cia408DefaultPdo(
            CanOpenLogicalDeviceAddressing.PdoNumber(LogicalDevice, pdoInDevice0), isTpdo, entries.AsReadOnly());
    }

    /// <summary>The mapping entry of an object: sub-index <c>00h</c> of a <c>VAR</c>, or the value at
    /// <c>01h</c> of a value record (§8.7.1). The length is that of its value type.</summary>
    private (ushort Index, byte Subindex, byte Bits) Entry(Cia408ObjectDefinition definition)
        => (CanOpenLogicalDeviceAddressing.Index(LogicalDevice, definition.Index),
            definition.IsValueRecord ? (byte)1 : (byte)0,
            checked((byte)(OdEntryLayout.FixedSize(definition.ValueType) * 8)));
}
