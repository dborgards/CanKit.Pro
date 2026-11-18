using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>One default PDO of CiA 401-1 v3.1.0 §6.2.5 to §6.2.12: which PDO, in which direction,
/// and what it maps. Every one is valid by default (<see cref="Cia401Configuration.DefaultPdos"/>).</summary>
/// <param name="Number">The absolute PDO number, <c>1</c> to <c>512</c>: already shifted by
/// <c>n·64</c> for logical device <c>n</c> (CiA 301 v4.2.0 §7.2.2.1).</param>
/// <param name="IsTpdo"><see langword="true"/> for a transmit PDO, <see langword="false"/> for a
/// receive PDO.</param>
/// <param name="Entries">The mapped entries in mapping order: absolute object index (already
/// shifted by <c>n·800h</c> for logical device <c>n</c>, CiA 301 v4.2.0 §7.4.1), sub-index and
/// length in bits. A read-only view, so the caller cannot change a default; it compares by
/// reference, as a list does, so compare its elements.</param>
internal readonly record struct Cia401DefaultPdo(
    int Number,
    bool IsTpdo,
    IReadOnlyList<(ushort Index, byte Subindex, byte Bits)> Entries);

/// <summary>
/// What one CiA 401 device has — channel counts per I/O function — and what follows from it as pure
/// computation, with no node involved: the device-type word of object <c>1000h</c>
/// (CiA 401-1 v3.1.0 §6.2.1) and the default PDOs (§6.1, §6.2.5 to §6.2.12).
/// </summary>
/// <remarks>
/// <para>Counts are channels, not sub-indices. A started group of eight digital channels is one
/// sub-index (§7.2.1.3), a group of four analog channels is one default PDO.</para>
/// <para><see cref="DeviceType"/>, <see cref="DefaultPdos"/> and <see cref="RequiredPdoCount"/>
/// call <see cref="Validate"/> themselves: a configuration that no device could have answers
/// nothing, it throws what <see cref="Validate"/> throws. The properties can be set only while the
/// object is created, but the object is not frozen against a caller who keeps a reference and sets
/// them again before reading; every read computes from the values at that moment.</para>
/// </remarks>
internal sealed class Cia401Configuration
{
    /// <summary>The digital channels one sub-index of an 8-bit digital object carries, a group
    /// (CiA 401-1 §7.2.1.3). The one place the device and the configuration take it from.</summary>
    internal const int ChannelsPerDigitalGroup = 8;

    /// <summary>The most digital channels one function can carry: <c>FEh</c> sub-indices of eight
    /// (CiA 401-1 §8.2.1, §8.3.1).</summary>
    public const int MaxDigitalChannels = Cia401Objects.HighestArraySubindex * ChannelsPerDigitalGroup;

    /// <summary>The most analog channels one function can carry: <c>FEh</c> sub-indices (CiA 401-1
    /// §8.4.2, §8.5.2).</summary>
    public const int MaxAnalogChannels = Cia401Objects.HighestArraySubindex;

    /// <summary>Bits 16 to 19 of the device type, the I/O functions implemented (CiA 401-1 §6.2.1,
    /// Table 2): the one place the configuration and the peer take them from.</summary>
    internal const uint DigitalInputsBit = 1u << 16;

    /// <summary>Bit 17 of the device type: digital outputs (see <see cref="DigitalInputsBit"/>).</summary>
    internal const uint DigitalOutputsBit = 1u << 17;

    /// <summary>Bit 18 of the device type: analog inputs (see <see cref="DigitalInputsBit"/>).</summary>
    internal const uint AnalogInputsBit = 1u << 18;

    /// <summary>Bit 19 of the device type: analog outputs (see <see cref="DigitalInputsBit"/>).</summary>
    internal const uint AnalogOutputsBit = 1u << 19;

    /// <summary>The sub-indices of eight digital channels PDO 1 carries: 64 channels, 8 bytes
    /// (CiA 401-1 §6.2.5, §6.2.6, §6.1).</summary>
    private const int DigitalGroupsInDefaultPdo = 8;

    /// <summary>The analog channels one default PDO carries (CiA 401-1 §6.2.7 to §6.2.12).</summary>
    private const int AnalogChannelsPerDefaultPdo = 4;

    /// <summary>PDO 2, 3 and 4 carry analog channels (CiA 401-1 §6.2.7 to §6.2.12).</summary>
    private const int FirstAnalogPdo = 2;
    private const int LastAnalogPdo = 4;

    private const byte DigitalBits = 8;
    private const byte AnalogBits = 16;

    /// <summary>Digital input channels, <c>0</c> to <see cref="MaxDigitalChannels"/> (<c>6000h</c>
    /// area, CiA 401-1 §8.2). Zero: the device has none.</summary>
    public int DigitalInputs { get; init; }

    /// <summary>Digital output channels, <c>0</c> to <see cref="MaxDigitalChannels"/> (CiA 401-1
    /// §8.3).</summary>
    public int DigitalOutputs { get; init; }

    /// <summary>Analog input channels, <c>0</c> to <see cref="MaxAnalogChannels"/> (CiA 401-1
    /// §8.4).</summary>
    public int AnalogInputs { get; init; }

    /// <summary>Analog output channels, <c>0</c> to <see cref="MaxAnalogChannels"/> (CiA 401-1
    /// §8.5).</summary>
    public int AnalogOutputs { get; init; }

    /// <summary>The logical device the objects and PDOs belong to, <c>0</c> to <c>7</c>: indices
    /// are <c>n·800h</c> higher, PDO numbers <c>n·64</c> higher (CiA 401-1 §6.2; CiA 301 v4.2.0
    /// §7.2.2.1, §7.4.1).</summary>
    public int LogicalDevice { get; init; }

    /// <summary>Checks that the configuration describes a device the norm allows.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count or the logical device is outside its
    /// range; the message names the property.</exception>
    /// <exception cref="ArgumentException">No I/O function is implemented: CiA 401-1 §6.2.1 has
    /// "at least one I/O functionality [of bits 16 to 19] shall be implemented".</exception>
    public void Validate()
    {
        CheckRange(DigitalInputs, MaxDigitalChannels, nameof(DigitalInputs));
        CheckRange(DigitalOutputs, MaxDigitalChannels, nameof(DigitalOutputs));
        CheckRange(AnalogInputs, MaxAnalogChannels, nameof(AnalogInputs));
        CheckRange(AnalogOutputs, MaxAnalogChannels, nameof(AnalogOutputs));
        CheckRange(LogicalDevice, CanOpenLogicalDeviceAddressing.MaxLogicalDevices - 1, nameof(LogicalDevice));

        if (DigitalInputs == 0 && DigitalOutputs == 0 && AnalogInputs == 0 && AnalogOutputs == 0)
        {
            throw new ArgumentException(
                "A CiA 401 device implements at least one I/O function (CiA 401-1 §6.2.1): "
                + "one of the digital inputs, digital outputs, analog inputs or analog outputs is not zero.");
        }
    }

    /// <summary>The device-type word of object <c>1000h</c>: profile <c>401</c>, bits 16 to 19 for
    /// the I/O functions that exist, the mapping bit M (23) clear for the pre-defined mapping and
    /// the specific functionality (bits 24 to 31) zero (CiA 401-1 §6.2.1, Tables 2 and 3). Calls
    /// <see cref="Validate"/>.</summary>
    /// <remarks>This is the word of this logical device itself. In a module with several logical
    /// devices, <c>1000h</c> carries <c>FFFFh</c> in the additional information and this word moves to
    /// <c>67FFh</c> + <c>n·800h</c> for logical device <c>n</c> (CiA 301 v4.2.0 §7.5.2.1; CiA 401-1
    /// §6.2.1). The node composes that; the configuration does not, and its value is the same in
    /// every logical device.</remarks>
    public CanOpenDeviceType DeviceType
    {
        get
        {
            Validate();
            var value = (uint)Cia401Profile.Number;
            if (DigitalInputs > 0)
            {
                value |= DigitalInputsBit;
            }

            if (DigitalOutputs > 0)
            {
                value |= DigitalOutputsBit;
            }

            if (AnalogInputs > 0)
            {
                value |= AnalogInputsBit;
            }

            if (AnalogOutputs > 0)
            {
                value |= AnalogOutputsBit;
            }

            return new CanOpenDeviceType(value);
        }
    }

    /// <summary>
    /// The default PDOs of the pre-defined mapping (CiA 401-1 §6.1, §6.2.5 to §6.2.12), ordered by
    /// number, the receive PDO before the transmit PDO of the same number. Calls
    /// <see cref="Validate"/>.
    /// </summary>
    /// <remarks>
    /// <para>PDO 1 carries the digital channels, one byte per started group of eight, up to eight
    /// (§6.2.5, §6.2.6); channels beyond 64 have no default PDO (§6.1). PDO 2, 3 and 4 carry the
    /// analog channels 1 to 4, 5 to 8 and 9 to 12 as 16-bit values, each only if a channel in it
    /// exists (§6.2.7 to §6.2.12); channels beyond twelve have no default PDO (§6.1). A function
    /// with no channel has no default PDO (§6.1).</para>
    /// <para>Every default PDO is valid, the TPDOs that carry analog inputs included: §6.2.8,
    /// §6.2.10 and §6.2.12 give TPDOs 2 to 4 a default COB-ID with bit 31 clear and transmission
    /// type 255 (Tables 18, 26, 34), and §6.1 sends every TPDO of that type on entering
    /// Operational. What is disabled by default is the analog interrupt, globally, by <c>6423h</c>
    /// FALSE (§6.2.8, §8.6.4, Table 154): with it, a changed analog input sends no TPDO, and the
    /// node warns with EMCY <c>0080h</c> on entering Operational (§5.3). §1's "Therefore, default
    /// TPDOs containing analog inputs are disabled" is read as that interrupt gate (#556).</para>
    /// </remarks>
    public IReadOnlyList<Cia401DefaultPdo> DefaultPdos
    {
        get
        {
            Validate();
            var pdos = new List<Cia401DefaultPdo>();

            // PDO 1 first, receive before transmit, then the analog PDOs one number at a time,
            // so that the list comes out ordered by number without a sort.
            AddDigital(pdos, isTpdo: false, DigitalOutputs, Cia401Objects.WriteDigitalOutput8.Index);
            AddDigital(pdos, isTpdo: true, DigitalInputs, Cia401Objects.ReadDigitalInput8.Index);

            for (var pdo = FirstAnalogPdo; pdo <= LastAnalogPdo; pdo++)
            {
                AddAnalog(pdos, pdo, isTpdo: false, AnalogOutputs, Cia401Objects.WriteAnalogOutput16.Index);
                AddAnalog(pdos, pdo, isTpdo: true, AnalogInputs, Cia401Objects.ReadAnalogInput16.Index);
            }

            return pdos;
        }
    }

    /// <summary>The highest PDO number, of either direction, that has a default: what the node has
    /// to provide for the default PDOs to be written (at least <c>1</c>, the node's minimum).
    /// Absolute, so it includes the <c>n·64</c> of a logical device. Calls
    /// <see cref="Validate"/>.</summary>
    public int RequiredPdoCount
    {
        get
        {
            var highest = 1;
            foreach (var pdo in DefaultPdos)
            {
                highest = Math.Max(highest, pdo.Number);
            }

            return highest;
        }
    }

    private void AddDigital(List<Cia401DefaultPdo> pdos, bool isTpdo, int channels, ushort indexInDevice0)
    {
        if (channels == 0)
        {
            return;
        }

        // A started group of eight is one sub-index; the default PDO has room for eight of them.
        var groups = Math.Min(
            (channels + ChannelsPerDigitalGroup - 1) / ChannelsPerDigitalGroup,
            DigitalGroupsInDefaultPdo);
        var index = CanOpenLogicalDeviceAddressing.Index(LogicalDevice, indexInDevice0);
        var entries = new List<(ushort Index, byte Subindex, byte Bits)>(groups);
        for (var subindex = 1; subindex <= groups; subindex++)
        {
            entries.Add((index, (byte)subindex, DigitalBits));
        }

        pdos.Add(new Cia401DefaultPdo(
            CanOpenLogicalDeviceAddressing.PdoNumber(LogicalDevice, 1), isTpdo, entries.AsReadOnly()));
    }

    private void AddAnalog(List<Cia401DefaultPdo> pdos, int pdoInDevice0, bool isTpdo, int channels, ushort indexInDevice0)
    {
        var firstChannel = ((pdoInDevice0 - FirstAnalogPdo) * AnalogChannelsPerDefaultPdo) + 1;
        if (channels < firstChannel)
        {
            return;
        }

        var count = Math.Min(AnalogChannelsPerDefaultPdo, channels - firstChannel + 1);
        var index = CanOpenLogicalDeviceAddressing.Index(LogicalDevice, indexInDevice0);
        var entries = new List<(ushort Index, byte Subindex, byte Bits)>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add((index, (byte)(firstChannel + i), AnalogBits));
        }

        pdos.Add(new Cia401DefaultPdo(
            CanOpenLogicalDeviceAddressing.PdoNumber(LogicalDevice, pdoInDevice0), isTpdo, entries.AsReadOnly()));
    }

    private static void CheckRange(int value, int max, string name)
    {
        if (value < 0 || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} is {value}: it lies in 0 to {max}.");
        }
    }
}
