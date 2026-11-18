using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// A device built from the node's device description: the file wins (scope record
// docs/reviews/2026-11-17-canopen-device-profiles-scope.md, "Regeln der Naht").
internal sealed partial class Cia401Device
{
    /// <summary>
    /// A device of logical device <paramref name="logicalDevice"/> built from the device description
    /// of the node it is attached to: it adopts the CiA 401 objects the file declares and serves the
    /// function blocks they make, so that a real EDS or DCF, which says which optional objects this
    /// device has, simulates exactly that device.
    /// </summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>: the file's objects are
    /// looked for at <c>6000h</c> to <c>67FFh</c> shifted by <c>n·800h</c> (CiA 301 v4.2.0 §7.4.1).</param>
    /// <param name="process">The plant model, as for the configuration's constructor; null for none.</param>
    /// <remarks>
    /// <para>
    /// <b>Function blocks.</b> When the node attaches the device, the file has already shaped the
    /// dictionary. A block exists when its mandatory object does: the digital inputs with
    /// <c>6000h</c>, the digital outputs with <c>6200h</c>, the analog inputs with <c>6401h</c>,
    /// the analog outputs with <c>6411h</c> (the 8-bit and 16-bit access of the first slice). The
    /// channels are what sub-index 00h announces: eight per sub-index for a digital block, one for
    /// an analog block (CiA 401-1 §7.2.1.3), and each sub-index up to it must be declared. A file
    /// with none of the four fails the open: CiA 401-1 §6.2.1 asks for at least one I/O function.
    /// </para>
    /// <para>
    /// <b>The file's objects.</b> Every object the file declares in the logical device's range is
    /// kept as the file has it: value, access and number of sub-indices. An object the catalogue
    /// knows (<see cref="Cia401Objects"/>) is checked first: an array must be one, with an UNSIGNED8
    /// sub-index 00h and the catalogue's data type in every element; a variable must be one, of the
    /// catalogue's data type. Access is not checked: the norm's <c>rw</c> allows <c>wo</c>,
    /// <c>ro</c> and <c>const</c>, its <c>ro</c> allows <c>const</c> (CiA 401-1 §8.1). The device's
    /// own writes to its input objects pass a read-only entry, as they pass the read-only
    /// <c>6000h</c> of a configured device.
    /// </para>
    /// <para>
    /// <b>What the file leaves out.</b> A first-slice parameter of a block that the file does not
    /// declare is not added, and the device behaves as its default value: "If an optional parameter
    /// is not implemented, the device shall behave as specified in the Default value attribute"
    /// (CiA 401-1 §8.1). Without <c>6006h</c>, every change of an input is an interrupt (FFh,
    /// §8.2.5); without <c>6422h</c> the device keeps no interrupt source. A parameter array shorter
    /// than its block leaves the channels beyond it at the default; a longer one has its surplus
    /// sub-indices stored and ignored.
    /// </para>
    /// <para>
    /// <b>Values.</b> The outputs and every parameter power on with the file's values, a DCF's
    /// ParameterValue over its DefaultValue (§6.2.5 NOTE), and Reset Node returns them there. The
    /// input objects (<c>6000h</c>, <c>6401h</c>, <c>6422h</c>) show the application's levels from
    /// the start, as after every Reset Node (see <see cref="Attach"/>), whatever value the file gives
    /// them.
    /// </para>
    /// <para>
    /// <b>Device type and PDOs.</b> The file's <c>1000h</c> stays, and the node checks that its
    /// profile number is 401; function bits (16 to 19) that disagree with the blocks found are left
    /// as the file has them and reported nowhere: the file wins. Without a <c>1000h</c> in the file,
    /// the node composes the one the blocks give (§6.2.1). The PDOs are the file's too: a description
    /// gives the node every PDO record it has, so the device sets no default PDO (see
    /// <see cref="ICanOpenApplicationHost.SetDefaultTpdo"/>), and it asks the node for no PDO count.
    /// </para>
    /// <para>
    /// <b>Objects the device shows and does not act on.</b> The file may declare objects of the
    /// logical device the first slice does not build: the bit, 16-bit and 32-bit digital access, the
    /// 8-bit, 32-bit, float and manufacturer-specific analog access, the float limits, offsets,
    /// scalings and SI units, and a parameter of a block whose mandatory object the file leaves out
    /// (<c>6443h</c> without <c>6411h</c>, say). They stay in the dictionary as stored objects: a
    /// master reads them, and writes them as their access allows, and nothing in the device follows.
    /// So the different accesses of one I/O do not "access a single database" (CiA 401-1 §7.2.1.2)
    /// for them; this is among the deviations listed on <see cref="Cia401Device"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static Cia401Device FromDescription(int logicalDevice = 0, ICia401Process? process = null)
    {
        if (logicalDevice < 0 || logicalDevice >= CanOpenLogicalDeviceAddressing.MaxLogicalDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalDevice), logicalDevice,
                $"The logical device is {logicalDevice}: it lies in 0 to {CanOpenLogicalDeviceAddressing.MaxLogicalDevices - 1}.");
        }

        return new Cia401Device(logicalDevice, process);
    }

    private Cia401Device(int logicalDevice, ICia401Process? process)
    {
        _fromDescription = true;
        _process = process;
        _logicalDevice = logicalDevice;
        _defaultPdos = Array.Empty<Cia401DefaultPdo>();
        _claims = new[] { CanOpenIndexRange.ForLogicalDevice(_logicalDevice) };
        Requirements = new CanOpenApplicationRequirements { CommunicationFailureMonitoring = true };
        Name = string.Format(CultureInfo.InvariantCulture, "CiA 401 device (logical device {0}, from the device description)", _logicalDevice);
    }

    /// <summary>
    /// Checks every object the description declared in the logical device against the catalogue,
    /// derives the function blocks from the mandatory objects, and returns the device type they give.
    /// Constructing thread, in Attach.
    /// </summary>
    /// <remarks>At Attach every entry in the claimed range was put there by the node's device
    /// description: no other application may claim the range, the node declares nothing in it
    /// before the applications have attached, and this device declares nothing on this path. So the
    /// dictionary is the description, and the device needs no query of the seam to tell.</remarks>
    private CanOpenDeviceType AdoptDescription(ObjectDictionary od)
    {
        var catalogue = new Dictionary<ushort, Cia401ObjectDefinition>();
        foreach (var definition in Cia401Objects.All) catalogue[Shift(_logicalDevice, definition)] = definition;
        foreach (var (index, subindices) in ClaimedObjects(od))
        {
            if (catalogue.TryGetValue(index, out var definition)) CheckAgainstCatalogue(od, index, subindices, definition);
        }

        var configuration = new Cia401Configuration
        {
            DigitalInputs = Elements(od, Cia401Objects.ReadDigitalInput8) * ChannelsPerGroup,
            DigitalOutputs = Elements(od, Cia401Objects.WriteDigitalOutput8) * ChannelsPerGroup,
            AnalogInputs = Elements(od, Cia401Objects.ReadAnalogInput16),
            AnalogOutputs = Elements(od, Cia401Objects.WriteAnalogOutput16),
            LogicalDevice = _logicalDevice,
        };
        if (configuration.DigitalInputs == 0 && configuration.DigitalOutputs == 0
            && configuration.AnalogInputs == 0 && configuration.AnalogOutputs == 0)
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares no CiA 401 function block for logical device {0}: none of {1:X4}h, {2:X4}h, " +
                "{3:X4}h and {4:X4}h, and a CiA 401 device implements at least one I/O function (CiA 401-1 §6.2.1).",
                _logicalDevice, Shift(_logicalDevice, Cia401Objects.ReadDigitalInput8),
                Shift(_logicalDevice, Cia401Objects.WriteDigitalOutput8), Shift(_logicalDevice, Cia401Objects.ReadAnalogInput16),
                Shift(_logicalDevice, Cia401Objects.WriteAnalogOutput16)));
        }

        _digitalInputs = configuration.DigitalInputs > 0 ? new DigitalInputBlock(configuration.DigitalInputs, _logicalDevice) : null;
        _digitalOutputs = configuration.DigitalOutputs > 0 ? new DigitalOutputBlock(configuration.DigitalOutputs, _logicalDevice) : null;
        _analogInputs = configuration.AnalogInputs > 0 ? new AnalogInputBlock(configuration.AnalogInputs, _logicalDevice) : null;
        _analogOutputs = configuration.AnalogOutputs > 0 ? new AnalogOutputBlock(configuration.AnalogOutputs, _logicalDevice) : null;
        return configuration.DeviceType;
    }

    /// <summary>A device built from a configuration declares its objects with the catalogue's
    /// defaults, which would replace what a description declared: it refuses a node whose
    /// description declares any object of its logical device, <c>67FFh</c> included, instead of
    /// replacing it. At Attach such an entry can only come from the description (see
    /// <see cref="AdoptDescription"/>). Constructing thread, in Attach.</summary>
    private void RefuseDescribedObjects(ObjectDictionary od)
    {
        foreach (var (index, _) in ClaimedObjects(od))
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares CiA 401 objects for logical device {0} ({1:X4}h among them), and {2} is " +
                "built from a configuration, which would replace them: use Cia401Device.FromDescription, so that the file wins.",
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

    /// <summary>
    /// The object code and data types of a declared object against the catalogue: an array with an
    /// UNSIGNED8 sub-index 00h (CiA 301 v4.2.0 §7.4.5) and the catalogue's type in every element
    /// (for a manufacturer-specific type, any), or a variable at sub-index 00h of the catalogue's
    /// type. The dictionary keeps no object code, so it is told from the shape: an array has
    /// elements above sub-index 00h, a variable has none.
    /// </summary>
    private void CheckAgainstCatalogue(ObjectDictionary od, ushort index, List<byte> subindices, Cia401ObjectDefinition definition)
    {
        if (definition.IsArray)
        {
            if (!od.TryGet(index, 0x00, out var count))
                throw ShapeError(index, definition, "without its sub-index 00h, the highest sub-index supported of an array");
            if (count.DataType != OdDataType.Unsigned8) throw TypeError(index, 0x00, OdDataType.Unsigned8, count.DataType, definition);
            if (subindices.All(subindex => subindex == 0x00)) throw ShapeError(index, definition, "as a variable, where the catalogue has an array");
            if (definition.IsManufacturerSpecific) return;
            foreach (var subindex in subindices.Where(subindex => subindex != 0x00))
            {
                od.TryGet(index, subindex, out var element);
                if (element.DataType != definition.DataType) throw TypeError(index, subindex, definition.DataType, element.DataType, definition);
            }

            return;
        }

        if (subindices.Any(subindex => subindex != 0x00)) throw ShapeError(index, definition, "as an array, where the catalogue has a variable");
        od.TryGet(index, 0x00, out var variable);
        if (variable.DataType != definition.DataType) throw TypeError(index, 0x00, definition.DataType, variable.DataType, definition);
    }

    /// <summary>The elements of a mandatory object, what its sub-index 00h announces, each of them
    /// declared; 0 when the object is not there. Read after the object was checked against the
    /// catalogue.</summary>
    private int Elements(ObjectDictionary od, Cia401ObjectDefinition definition)
    {
        var index = Shift(_logicalDevice, definition);
        if (!od.ContainsIndex(index)) return 0;
        int count = od.ReadRaw(index, 0x00)[0];
        if (count < 1 || count > definition.MaxSubindex)
        {
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares {0:X4}h ({1}) with {2} in sub-index 00h: a function block has 1 to {3} " +
                "sub-indices (logical device {4}).", index, definition.Name, count, definition.MaxSubindex, _logicalDevice));
        }

        for (int subindex = 1; subindex <= count; subindex++)
        {
            if (od.TryGet(index, (byte)subindex, out _)) continue;
            throw OptionsError(string.Format(CultureInfo.InvariantCulture,
                "The device description declares {0:X4}h ({1}) with {2} in sub-index 00h, but not its sub-index {3:X2}h " +
                "(logical device {4}).", index, definition.Name, count, subindex, _logicalDevice));
        }

        return count;
    }

    private ArgumentException TypeError(ushort index, byte subindex, OdDataType expected, OdDataType found, Cia401ObjectDefinition definition)
        => OptionsError(string.Format(CultureInfo.InvariantCulture,
            "The device description declares {0:X4}h sub-index {1:X2}h as {2}, where {3} gives {4} the data type {5} there " +
            "(logical device {6}).", index, subindex, found, Clause(definition), definition.Name, expected, _logicalDevice));

    private ArgumentException ShapeError(ushort index, Cia401ObjectDefinition definition, string how)
        => OptionsError(string.Format(CultureInfo.InvariantCulture,
            "The device description declares {0:X4}h ({1}) {2} ({3}, logical device {4}).",
            index, definition.Name, how, Clause(definition), _logicalDevice));

    private static string Clause(Cia401ObjectDefinition definition)
        => char.IsDigit(definition.Clause[0])
            ? "CiA 401-1 §" + definition.Clause
            : "CiA 401-1, " + definition.Clause;

    /// <summary>What the node throws for an application that fails the open: the parameter is
    /// <c>options</c>, which carries the applications.</summary>
    private static ArgumentException OptionsError(string message) => new(message, "options");
}
