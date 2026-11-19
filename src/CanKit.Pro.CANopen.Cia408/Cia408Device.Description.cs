using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

// A device built from the node's device description: the file wins (scope record
// docs/reviews/2026-11-17-canopen-device-profiles-scope.md, "Regeln der Naht").
internal sealed partial class Cia408Device
{
    /// <summary>The device-type object of a single logical device, <c>1000h</c> (CiA 301 v4.2.0
    /// §7.5.2.1); a node with several holds each device's word in <c>67FFh + n·800h</c>
    /// (<see cref="CanOpenLogicalDevices.DeviceTypeIndex"/>).</summary>
    private const ushort NodeDeviceType = 0x1000;

    // Whether the device takes its objects from the node's device description (FromDescription)
    // rather than from a configuration.
    private readonly bool _fromDescription;

    // Whether the device has the objects §9.3 does not make mandatory for every device: the 32-bit
    // word pair (6044h, 6045h), 604Eh, 604Fh and 6043h. A device built from a configuration declares
    // all of them; one built from a description has what the file declares (AdoptDescription).
    // Written in Attach, before the host is published.
    private bool _has32 = true;
    private bool _hasErrorCode = true;
    private bool _hasLocal = true;
    private bool _hasControlMode = true;

    /// <summary>
    /// A device of logical device <paramref name="logicalDevice"/> built from the device description
    /// of the node it is attached to: it adopts the CiA 408 objects the file declares, with the file's
    /// types, access and values, and derives its resolution and modes from them, so that a real EDS or
    /// DCF simulates exactly that device.
    /// </summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>: the file's objects are
    /// looked for at <c>6000h</c> to <c>67FFh</c> shifted by <c>n·800h</c> (CiA 301 v4.2.0 §7.4.1).</param>
    /// <param name="process">The process model, as for the configuration's constructor; null for
    /// none. It steps at <see cref="Cia408Configuration.DefaultProcessInterval"/>.</param>
    /// <remarks>
    /// <para>
    /// <b>The device exists</b> when the file declares the objects §9.3 makes mandatory for it:
    /// <c>6040h</c>, <c>6041h</c> and <c>6042h</c> always, and the 32-bit pair <c>6044h</c> and
    /// <c>6045h</c> when the device has 32-bit parameters. The norm calls both pairs Mandatory although
    /// a device uses one resolution (§8.6.2, Table 53, NOTE 2); the reading taken is that the 16-bit
    /// pair is always there and the 32-bit pair where the file's device type asks for it — dtprs 10b
    /// or 11b, both of which give 32-bit parameters to some function of the device. A file without a
    /// device type has no such statement, and the 32-bit pair is then adopted where it is declared and
    /// required nowhere. Any of the required objects missing fails the open.
    /// </para>
    /// <para>
    /// <b>The device type</b> is the file's: <c>67FFh + n·800h</c> when the file declares it, else
    /// <c>1000h</c> when it names profile 408 and announces no further logical device. Its resolution
    /// field must not be the reserved 00b. A file with neither gives the device type the objects make:
    /// a capability bit for every control mode found below, and the resolution of the widths they were
    /// found in — 10b where a valve mode's 32-bit setpoints and actual values are declared, 11b where
    /// 16-bit valve modes and drive modes are, 10b for drive modes alone, 01b for 16-bit valve modes
    /// alone. The word pair declared does not decide the resolution: the norm has every device declare
    /// both. The
    /// node keeps a described device type and checks its profile number
    /// (<see cref="ICanOpenApplicationHost.SetDeviceType"/>).
    /// </para>
    /// <para>
    /// <b>Control modes.</b> With a device type from the file, they are the modes its capability bits
    /// announce (<see cref="Cia408Configuration.ControlModesOf"/>), where bit 23 (spool position) and
    /// bit 24 (pressure) each name an open-loop and a closed-loop mode the word cannot tell apart: the
    /// open-loop mode (1, 3) is taken always, the closed-loop mode (2, 4) when the file declares its
    /// actual value — the object the norm adds for closed loop (§9.8.3, §9.12.3: Conditional on mode 2,
    /// mode 4). Every mode taken must have the setpoints and actual values of its default PDO mapping
    /// declared (<see cref="Cia408Configuration.SetpointsOf"/>, <see cref="Cia408Configuration.ActualValuesOf"/>),
    /// Conditional objects the norm makes mandatory with the mode; one missing fails the open. Without a
    /// device type, the modes are those whose objects the file declares, at least one of them, in the
    /// width the resolution gives them: a mode without such objects (11, 12, 15) cannot be found that
    /// way, and every mode whose objects are there is taken (a file with <c>6300h</c> and <c>6301h</c>
    /// supports modes 1 and 2). The default control mode is the
    /// value of the file's <c>6043h</c>, which must be one of them, and the lowest mode where the file
    /// has no <c>6043h</c>.
    /// </para>
    /// <para>
    /// <b>Device modes.</b> A file cannot list them. A read-only <c>6042h</c> means one device mode
    /// (§9.3.4), its value. A writable one takes every mode Table 79 gives the device's kind — 0 to 2
    /// for a valve, 0 to 6 with a drive control mode — and the file's own value if that is another, a
    /// manufacturer-specific one say.
    /// </para>
    /// <para>
    /// <b>The file's objects.</b> Every object the file declares in the logical device's range stays as
    /// the file has it. One the catalogue knows (<see cref="Cia408Objects"/>) is checked first: a
    /// <c>VAR</c> must be a variable at sub-index 00h of the catalogue's value type; a value record
    /// must be a record with sub-index 00h (UNSIGNED8) and 01h (the catalogue's value type), and 02h
    /// (UNSIGNED8) and 03h (INTEGER8) where declared, and no other (§8.7.1). Access is not checked: the
    /// norm's "rw or ro" leaves it to the manufacturer. Anything else fails the open.
    /// </para>
    /// <para>
    /// <b>What the file leaves out</b> is not added: no <c>6043h</c>, <c>604Eh</c>, <c>604Fh</c> or
    /// 32-bit pair the file does not declare, and no setpoint or actual value. The device then behaves
    /// as if the object held its default, the reading CiA 401-1 v3.1.0 §8.1 states and CiA 408 leaves
    /// unsaid: without <c>604Fh</c> the control word comes from the bus for a valve and from the local
    /// source for a drive (§9.3.9), without <c>6043h</c> the default control mode runs, without
    /// <c>604Eh</c> the code of a fault or warning is shown nowhere but in its EMCY.
    /// </para>
    /// <para>
    /// <b>Values.</b> The parameters — the modes, <c>604Fh</c>, the setpoints, unit and prefix — power
    /// on with the file's values (of a DCF the ParameterValue, CiA 306-1 v1.4.0 §7.3.1). What shows the
    /// device's state is the device's from the start, as after every Reset Node
    /// (<see cref="OnReset"/>): the status words composed from the state, the control words 0, which
    /// start the history of the control word, <c>604Eh</c> 0, and the actual values the device holds,
    /// 0 until the model or the application sets one.
    /// </para>
    /// <para>
    /// <b>PDOs</b> are the file's, none included: the device sets no default PDO and asks the node for
    /// no PDO count. A file without PDO records gives a device that is driven and read over SDO.
    /// </para>
    /// <para>
    /// <b>1029h</b> is the CiA 408 record (§8.6.8, Table 56): the device asks for its layout
    /// (<see cref="ErrorBehaviorLayout"/>), and the node fails the open when the file declares
    /// <c>1029h</c> without the internal device error at sub-index 02h.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static Cia408Device FromDescription(int logicalDevice = 0, ICia408Process? process = null)
    {
        if (logicalDevice < 0 || logicalDevice >= CanOpenLogicalDeviceAddressing.MaxLogicalDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalDevice), logicalDevice,
                $"The logical device is {logicalDevice}: it lies in 0 to {CanOpenLogicalDeviceAddressing.MaxLogicalDevices - 1}.");
        }

        return new Cia408Device(logicalDevice, process);
    }

    private Cia408Device(int logicalDevice, ICia408Process? process)
    {
        _fromDescription = true;
        _process = process;
        _interval = Cia408Configuration.DefaultProcessInterval;
        _logicalDevice = logicalDevice;
        _defaultPdos = Array.Empty<Cia408DefaultPdo>();
        _claims = new[] { CanOpenIndexRange.ForLogicalDevice(_logicalDevice) };

        _controlWord16 = Shift(Cia408Objects.DeviceControlWord16);
        _statusWord16 = Shift(Cia408Objects.DeviceStatusWord16);
        _deviceMode = Shift(Cia408Objects.DeviceMode);
        _controlMode = Shift(Cia408Objects.DeviceControlMode);
        _controlWord32 = Shift(Cia408Objects.DeviceControlWord32);
        _statusWord32 = Shift(Cia408Objects.DeviceStatusWord32);
        _errorCode = Shift(Cia408Objects.DeviceErrorCode);
        _local = Shift(Cia408Objects.DeviceLocal);
        _commandEntries = Array.Empty<CanOpenObjectAddress>();

        Requirements = new CanOpenApplicationRequirements
        {
            CommunicationFailureMonitoring = true,
            ErrorBehavior = ErrorBehaviorLayout,
        };
        Name = string.Format(CultureInfo.InvariantCulture, "CiA 408 device (logical device {0}, from the device description)", _logicalDevice);
    }

    /// <summary>
    /// Checks every object the description declared in the logical device against the catalogue,
    /// requires the mandatory objects, derives the resolution and the modes (see
    /// <see cref="FromDescription"/>), writes what shows the device's state, and returns the device type.
    /// Constructing thread, in Attach, before the host is published.
    /// </summary>
    /// <remarks>At Attach every entry in the claimed range was put there by the node's device
    /// description: no other application may claim the range, the node declares nothing in it before
    /// the applications have attached, and this device declares nothing on this path. So the
    /// dictionary is the description.</remarks>
    private CanOpenDeviceType AdoptDescription(ICanOpenApplicationHost host)
    {
        var od = host.ObjectDictionary;
        foreach (var (index, subindices) in ClaimedObjects(od))
        {
            int indexInDevice0 = index - (_controlWord16 - Cia408Objects.DeviceControlWord16.Index);
            if (Cia408Objects.TryGet((ushort)indexInDevice0, out var definition)) CheckAgainstCatalogue(od, index, subindices, definition);
        }

        Require(od, Cia408Objects.DeviceControlWord16);
        Require(od, Cia408Objects.DeviceStatusWord16);
        Require(od, Cia408Objects.DeviceMode);

        var fileType = DescribedDeviceType(od);
        Cia408Resolution resolution;
        List<Cia408ControlMode> modes;
        if (fileType is { } described)
        {
            resolution = Cia408Configuration.ResolutionOf(described) ?? throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description gives logical device {0} the device type {1}, whose resolution field dtprs is the " +
                "reserved 00b (CiA 408 §8.6.2, Table 53).", _logicalDevice, described));
            modes = AnnouncedModes(od, Shape(resolution), described);
            if (resolution != Cia408Resolution.Valve16)
            {
                Require(od, Cia408Objects.DeviceControlWord32);
                Require(od, Cia408Objects.DeviceStatusWord32);
            }
        }
        else
        {
            (resolution, modes) = DeclaredModes(od);
        }

        _has32 = od.ContainsIndex(_controlWord32) && od.ContainsIndex(_statusWord32);
        var shape = Shape(resolution);

        _hasControlMode = od.ContainsIndex(_controlMode);
        if (_hasControlMode)
        {
            var current = (Cia408ControlMode)unchecked((sbyte)od.ReadRaw(_controlMode, 0x00)[0]);
            if (!modes.Remove(current))
            {
                throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                    "The device description gives {0:X4}h ({1}) the value {2}, which is no control mode the file supports: " +
                    "{3} (logical device {4}).", _controlMode, Cia408Objects.DeviceControlMode.Name, (int)current,
                    string.Join(", ", modes.Select(m => ((int)m).ToString(CultureInfo.InvariantCulture))), _logicalDevice));
            }

            modes.Insert(0, current);
        }

        bool drive = modes.Any(Cia408Configuration.IsDriveMode);
        foreach (var mode in modes)
        {
            _controlModes.Add((sbyte)mode);
            foreach (var setpoint in shape.SetpointsOf(mode)) AddRecord(_setpoints, setpoint);
            foreach (var actual in shape.ActualValuesOf(mode)) AddRecord(_actuals, actual);
        }

        var deviceMode = unchecked((sbyte)od.ReadRaw(_deviceMode, 0x00)[0]);
        if (od.TryGet(_deviceMode, 0x00, out var deviceModeEntry) && (deviceModeEntry.Access & OdAccess.WriteOnly) != 0)
        {
            var last = drive ? Cia408DeviceMode.AutomaticSingleStep : Cia408DeviceMode.SetpointLocal;
            for (var mode = Cia408DeviceMode.NoDeviceMode; mode <= last; mode++) _deviceModes.Add((sbyte)mode);
        }

        _deviceModes.Add(deviceMode);

        _controlModeDefault = (sbyte)modes[0];
        _localDefault = Cia408Configuration.IsDriveMode(modes[0]) ? (sbyte)1 : (sbyte)0;
        _hasErrorCode = od.ContainsIndex(_errorCode);
        _hasLocal = od.ContainsIndex(_local);
        _localSource = (_hasLocal ? unchecked((sbyte)od.ReadRaw(_local, 0x00)[0]) : _localDefault) == 1;

        var commands = new List<CanOpenObjectAddress> { new(_controlWord16, 0x00) };
        if (_has32) commands.Add(new CanOpenObjectAddress(_controlWord32, 0x00));
        if (_hasLocal) commands.Add(new CanOpenObjectAddress(_local, 0x00));
        _commandEntries = commands.ToArray();

        // What shows the device's state is the device's from the start (see FromDescription).
        WriteOwn(host, _controlWord16, Encode(0, 2));
        if (_has32) WriteOwn(host, _controlWord32, Encode(0, 4));
        if (_hasErrorCode) WriteOwn(host, _errorCode, Encode(0, 2));
        var status = ComposeStatus();
        WriteOwn(host, _statusWord16, Encode(status, 2));
        if (_has32) WriteOwn(host, _statusWord32, Encode(status, 4));
        RewriteActualValues(host);

        if (fileType is { } kept) return kept;
        return new Cia408Configuration { Resolution = resolution, ControlModes = modes }.DeviceType;
    }

    /// <summary>The control modes the file's device type announces, bits 23 and 24 split by the
    /// closed-loop actual value, each with its objects required (see <see cref="FromDescription"/>).</summary>
    private List<Cia408ControlMode> AnnouncedModes(ObjectDictionary od, Cia408Configuration shape, CanOpenDeviceType deviceType)
    {
        var modes = new List<Cia408ControlMode>();
        foreach (var mode in Cia408Configuration.ControlModesOf(deviceType))
        {
            if (mode is Cia408ControlMode.SpoolPositionClosedLoop or Cia408ControlMode.PressureClosedLoop
                && !shape.ActualValuesOf(mode).All(actual => od.ContainsIndex(Shift(actual))))
            {
                continue;
            }

            foreach (var record in shape.SetpointsOf(mode).Concat(shape.ActualValuesOf(mode)))
            {
                if (od.ContainsIndex(Shift(record))) continue;
                throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                    "The device description gives logical device {0} the device type {1}, which announces control mode {2} " +
                    "({3}), and does not declare {4:X4}h ({5}), which {6} makes mandatory with it.",
                    _logicalDevice, deviceType, (int)mode, mode, Shift(record), record.Name, Clause(record)));
            }

            modes.Add(mode);
        }

        if (modes.Count == 0)
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description gives logical device {0} the device type {1}, which announces no control mode: " +
                "a CiA 408 device supports at least one (CiA 408 §8.6.2, Table 53; §9.3.5).", _logicalDevice, deviceType));
        }

        return modes;
    }

    /// <summary>Without a device type in the file: the control modes whose setpoints and actual values
    /// the file declares, each found in the width it has, and the resolution they make (see
    /// <see cref="FromDescription"/>).</summary>
    private (Cia408Resolution Resolution, List<Cia408ControlMode> Modes) DeclaredModes(ObjectDictionary od)
    {
        var narrow = Shape(Cia408Resolution.Valve16);
        var wide = Shape(Cia408Resolution.Valve32Drive32);
        var valve16 = new List<Cia408ControlMode>();
        var valve32 = new List<Cia408ControlMode>();
        var drives = new List<Cia408ControlMode>();
        for (var mode = Cia408ControlMode.SpoolPositionOpenLoop; mode <= Cia408ControlMode.PFlowWithPowerLimitation; mode++)
        {
            if (Cia408Configuration.IsDriveMode(mode))
            {
                if (Declares(od, wide, mode)) drives.Add(mode);
                continue;
            }

            if (Declares(od, narrow, mode)) valve16.Add(mode);
            if (Declares(od, wide, mode)) valve32.Add(mode);
        }

        // Table 53: 01b valve 16-bit; 10b valve and drive 32-bit; 11b valve 16-bit, drive 32-bit.
        var resolution = valve32.Count > 0 ? Cia408Resolution.Valve32Drive32
            : drives.Count > 0 && valve16.Count > 0 ? Cia408Resolution.Valve16Drive32
            : drives.Count > 0 ? Cia408Resolution.Valve32Drive32
            : Cia408Resolution.Valve16;
        var modes = (resolution == Cia408Resolution.Valve32Drive32 ? valve32 : valve16).Concat(drives).OrderBy(mode => mode).ToList();
        if (modes.Count == 0)
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares no device type for logical device {0} and no control mode's setpoints and " +
                "actual values, so no control mode can be told: a CiA 408 device supports at least one (CiA 408 §9.3.5).",
                _logicalDevice));
        }

        return (resolution, modes);
    }

    /// <summary>Whether the file declares every setpoint and actual value of <paramref name="mode"/>'s
    /// default mapping in <paramref name="shape"/>'s width, and there is at least one.</summary>
    private bool Declares(ObjectDictionary od, Cia408Configuration shape, Cia408ControlMode mode)
    {
        var records = shape.SetpointsOf(mode).Concat(shape.ActualValuesOf(mode)).ToList();
        return records.Count > 0 && records.All(record => od.ContainsIndex(Shift(record)));
    }

    /// <summary>A configuration of <paramref name="resolution"/>, for the widths it gives each control
    /// mode's setpoints and actual values (<see cref="Cia408Configuration.SetpointsOf"/>).</summary>
    private static Cia408Configuration Shape(Cia408Resolution resolution)
        => new() { Resolution = resolution, ControlModes = new[] { Cia408ControlMode.SpoolPositionOpenLoop } };

    /// <summary>The file's device-type word of this logical device, or null where the file declares
    /// none (see <see cref="FromDescription"/>).</summary>
    private CanOpenDeviceType? DescribedDeviceType(ObjectDictionary od)
    {
        if (od.TryReadRaw(CanOpenLogicalDevices.DeviceTypeIndex(_logicalDevice), 0x00, out var own)
            && CanOpenDeviceType.TryParse(own, out var word))
        {
            return word;
        }

        if (_logicalDevice == 0 && od.TryReadRaw(NodeDeviceType, 0x00, out var node) && CanOpenDeviceType.TryParse(node, out var single)
            && single.ProfileNumber == Cia408Profile.Number && !single.HasFurtherLogicalDevices)
        {
            return single;
        }

        return null;
    }

    /// <summary>A device built from a configuration declares its objects with the catalogue's
    /// defaults, which would replace what a description declared: it refuses a node whose
    /// description declares any object of its logical device instead. The device-type object
    /// <c>67FFh + n·800h</c> does not count: the device never declares it, and the node keeps a
    /// described one, checking its profile number. Constructing thread, in Attach.</summary>
    private void RefuseDescribedObjects(ObjectDictionary od)
    {
        var deviceTypeObject = CanOpenLogicalDevices.DeviceTypeIndex(_logicalDevice);
        foreach (var (index, _) in ClaimedObjects(od).Where(o => o.Index != deviceTypeObject))
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares CiA 408 objects for logical device {0} ({1:X4}h among them), and {2} is " +
                "built from a configuration, which would replace them: use Cia408Device.FromDescription, so that the file wins.",
                _logicalDevice, index, Name));
        }
    }

    /// <summary>Every object in the claimed range, in index order, with its sub-indices.</summary>
    private IEnumerable<(ushort Index, List<byte> Subindices)> ClaimedObjects(ObjectDictionary od)
        => od.SnapshotKeys()
            .Select(key => (Index: (ushort)(key >> 8), Subindex: (byte)(key & 0xFF)))
            .Where(entry => _claims.Any(range => range.Contains(entry.Index)))
            .GroupBy(entry => entry.Index)
            .Select(group => (group.Key, group.Select(entry => entry.Subindex).ToList()));

    /// <summary>The object kind and data types of a declared object against the catalogue (see
    /// <see cref="FromDescription"/>). The dictionary keeps no object code, so the kind is told from
    /// the shape: a record has entries above sub-index 00h, a variable has none.</summary>
    private void CheckAgainstCatalogue(ObjectDictionary od, ushort index, List<byte> subindices, Cia408ObjectDefinition definition)
    {
        if (!definition.IsValueRecord)
        {
            if (subindices.Any(subindex => subindex != 0x00)) throw ShapeError(index, definition, "as a record, where the catalogue has a VAR");
            od.TryGet(index, 0x00, out var variable);
            if (variable.DataType != definition.ValueType) throw TypeError(index, 0x00, definition.ValueType, variable.DataType, definition);
            return;
        }

        if (!subindices.Contains(0x01)) throw ShapeError(index, definition, "without its value at sub-index 01h, where the catalogue has a value record");
        foreach (var subindex in subindices)
        {
            OdDataType expected = subindex switch
            {
                0x00 or 0x02 => OdDataType.Unsigned8,
                0x01 => definition.ValueType,
                0x03 => OdDataType.Integer8,
                _ => throw ShapeError(index, definition, string.Format(CultureInfo.InvariantCulture,
                    "with sub-index {0:X2}h, where a value record has 00h to 03h (CiA 408 §8.7.1)", subindex)),
            };
            od.TryGet(index, subindex, out var entry);
            if (entry.DataType != expected) throw TypeError(index, subindex, expected, entry.DataType, definition);
        }

        if (!subindices.Contains(0x00)) throw ShapeError(index, definition, "without its sub-index 00h, the highest sub-index supported of a record");
    }

    /// <summary>Fails the open when the file does not declare <paramref name="definition"/>, which
    /// §9.3 makes mandatory for this device (see <see cref="FromDescription"/>).</summary>
    private void Require(ObjectDictionary od, Cia408ObjectDefinition definition)
    {
        var index = Shift(definition);
        if (od.ContainsIndex(index)) return;
        throw OptionsError(string.Format(CultureInfo.InvariantCulture,
            "The device description does not declare {0:X4}h ({1}), which {2} makes mandatory for this device (logical device {3}).",
            index, definition.Name, Clause(definition), _logicalDevice));
    }

    private ArgumentException TypeError(ushort index, byte subindex, OdDataType expected, OdDataType found, Cia408ObjectDefinition definition)
        => OptionsError(string.Format(CultureInfo.InvariantCulture,
            "The device description declares {0:X4}h sub-index {1:X2}h as {2}, where {3} gives {4} the data type {5} there " +
            "(logical device {6}).", index, subindex, found, Clause(definition), definition.Name, expected, _logicalDevice));

    private ArgumentException ShapeError(ushort index, Cia408ObjectDefinition definition, string how)
        => OptionsError(string.Format(CultureInfo.InvariantCulture,
            "The device description declares {0:X4}h ({1}) {2} ({3}, logical device {4}).",
            index, definition.Name, how, Clause(definition), _logicalDevice));

    private static string Clause(Cia408ObjectDefinition definition) => "CiA 408 §" + definition.Clause;

    /// <summary>What the node throws for an application that fails the open: the parameter is
    /// <c>options</c>, which carries the applications.</summary>
    private static ArgumentException OptionsError(string message) => new(message, "options");
}
