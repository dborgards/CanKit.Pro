using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// The device type of a node with profiles (CiA 301 v4.2.0 §7.5.2.1): what 1000h and the chain of
// 67FFh, 6FFFh... hold when applications set the device types of their logical devices.
public partial class CanOpenApplicationSeamTests
{
    private const uint Profile401 = 0x000F0191;
    private const uint Profile408 = 0x00010198;

    /// <summary>A profile that claims the area of every logical device it sets a type for, and sets
    /// them in <c>Attach</c>, in the order given.</summary>
    private static RecordingApplication Typed(string name, params (int Device, uint Word)[] types)
        => new(name, types.Select(t => t.Device).Distinct().Select(CanOpenIndexRange.ForLogicalDevice).ToArray())
        {
            OnAttach = host =>
            {
                foreach (var (device, word) in types) host.SetDeviceType(device, new CanOpenDeviceType(word));
            },
        };

    private static uint DeviceTypeOf(CanOpenNode node, ushort index = 0x1000)
        => node.ObjectDictionary.ReadUnsigned(index, 0x00);

    /// <summary>Opens a node that must not open, and returns what it threw.</summary>
    private static TException OpenThrows<TException>(CanOpenNodeOptions options,
        CanOpenDeviceDescription? description = null)
        where TException : Exception
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        return Assert.Throws<TException>(
            () => { _ = new CanOpenNode(service, Slave, options, ownsService: false, description: description); });
    }

    private static ArgumentException OptionsRefused(CanOpenNodeOptions options,
        CanOpenDeviceDescription? description = null)
    {
        var failure = OpenThrows<ArgumentException>(options, description);
        failure.ParamName.Should().Be("options");
        return failure;
    }

    [Fact]
    public void One_Profile_Sets_1000h()
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("one", (0, Profile401))));

        DeviceTypeOf(node).Should().Be(Profile401);
        node.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse("a node with one logical device has no chain");
    }

    [Fact]
    public void Two_Profiles_Announce_Further_Devices()
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("two", (0, Profile401), (1, Profile408))));

        DeviceTypeOf(node).Should().Be(0xFFFF0191u, "profile of device 0 in bits 0..15, FFFFh above");
        DeviceTypeOf(node, 0x67FF).Should().Be(Profile401, "device 0 keeps its additional information in 67FFh");
        DeviceTypeOf(node, 0x6FFF).Should().Be(Profile408);
        node.ObjectDictionary.ContainsIndex(0x77FF).Should().BeFalse();
        foreach (ushort index in new ushort[] { 0x67FF, 0x6FFF })
        {
            node.ObjectDictionary.TryGet(index, 0x00, out var entry).Should().BeTrue();
            entry.DataType.Should().Be(OdDataType.Unsigned32);
            entry.Access.Should().Be(OdAccess.ReadOnly);
        }
    }

    [Fact]
    public async Task The_Chain_Reads_Back_Over_The_Bus()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        master.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF));
        var bootups = new BootupWatch(master, Slave);
        using var slave = OpenNode(busB, With(Typed("two", (0, Profile401), (1, Profile408))));
        await bootups.First;

        var result = await CanOpenLogicalDevices.ReadAsync(master, Slave).WithTimeoutAsync(ShortTimeout);

        result.DeviceType.Value.Should().Be(0xFFFF0191u);
        result.Devices.Select(d => (d.Index, d.DeviceType.Value)).Should().Equal((0, Profile401), (1, Profile408));
    }

    [Fact]
    public void Without_A_Profile_1000h_Is_Left_Alone()
    {
        using var busA = Open(NewSession(), 1);
        using var busB = Open(NewSession(), 1);
        using var busC = Open(NewSession(), 1);
        using var plain = OpenNode(busA, new CanOpenNodeOptions());
        var silent = new RecordingApplication("silent", CanOpenIndexRange.ForLogicalDevice(0));
        // The placeholder is 0, which a stray write of 0 would leave unchanged: this one puts
        // another word there itself, through the dictionary, and never calls SetDeviceType.
        const uint Own = 0x00070123;
        var writer = new RecordingApplication("writer", CanOpenIndexRange.ForLogicalDevice(0))
        {
            OnAttach = host => host.ObjectDictionary.WriteUnsigned(0x1000, 0x00, Own),
        };

        using var quiet = OpenNode(busB, With(silent));
        using var written = OpenNode(busC, With(writer));

        DeviceTypeOf(quiet).Should().Be(DeviceTypeOf(plain));
        DeviceTypeOf(written).Should().Be(Own, "what the application wrote is not composed over");
        quiet.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse();
        written.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse();
    }

    [Fact]
    public void A_Gap_In_The_Logical_Devices_Fails_The_Open()
    {
        var failure = OptionsRefused(With(Typed("gappy", (0, Profile401), (2, Profile408))));

        failure.Message.Should().Contain("Logical device 1").And.Contain("gappy");
    }

    [Fact]
    public void A_Missing_First_Logical_Device_Fails_The_Open()
    {
        OptionsRefused(With(Typed("late", (1, Profile408)))).Message.Should().Contain("Logical device 0");
    }

    [Fact]
    public void The_Same_Logical_Device_Twice_Fails_The_Open()
    {
        var failure = OptionsRefused(With(Typed("twice", (0, Profile401), (0, Profile408))));

        failure.Message.Should().Contain("Logical device 0");
        failure.Message.Split("'twice'").Length.Should().Be(3, "it names both setters, which are one application here");
    }

    [Fact]
    public void A_Device_Type_Outside_The_Claim_Is_Refused()
    {
        var app = new RecordingApplication("narrow", Claim6000)
        {
            OnAttach = host => host.SetDeviceType(1, new CanOpenDeviceType(Profile408)),
        };

        var failure = OpenThrows<ArgumentException>(With(app));

        failure.GetType().Should().Be<ArgumentException>();
        failure.ParamName.Should().Be("logicalDevice");
        failure.Message.Should().Contain("narrow").And.Contain("6FFF");
    }

    [Fact]
    public void A_Logical_Device_Outside_0_To_7_Is_Refused()
    {
        var app = new RecordingApplication("wide", new CanOpenIndexRange(0x6000, 0x9FFF))
        {
            OnAttach = host => host.SetDeviceType(8, new CanOpenDeviceType(Profile401)),
        };

        OpenThrows<ArgumentOutOfRangeException>(With(app)).ParamName.Should().Be("logicalDevice");
    }

    [Fact]
    public async Task Setting_The_Type_After_Attach_Is_Refused()
    {
        var app = Entries();
        app.WhenWritten = _ => app.Host!.SetDeviceType(0, new CanOpenDeviceType(Profile401));
        using var rig = new EntryRig(With(app));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        failure.Should().BeOfType<InvalidOperationException>();
        DeviceTypeOf(rig.Node).Should().Be(0u, "the refused call changed nothing");
    }

    [Fact]
    public async Task Reset_Node_Restores_The_Composed_Chain_After_It_Was_Overwritten()
    {
        using var rig = new ResetRig(Typed("two", (0, Profile401), (1, Profile408)));
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x67FF, 0x00, 0);
        rig.Od.WriteUnsigned(0x6FFF, 0x00, 0);

        await rig.ResetAsync(NmtCommand.ResetNode);

        DeviceTypeOf(rig.SlaveNode, 0x67FF).Should().Be(Profile401, "the composed word is the power-on value");
        DeviceTypeOf(rig.SlaveNode, 0x6FFF).Should().Be(Profile408);
        DeviceTypeOf(rig.SlaveNode).Should().Be(0xFFFF0191u);
    }

    [Fact]
    public async Task Restoring_Defaults_Returns_The_Chain_To_The_Composed_Words()
    {
        using var rig = new ResetRig(Typed("two", (0, Profile401), (1, Profile408)));
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6FFF, 0x00, 0);
        rig.SlaveNode.StoreParameters();
        rig.SlaveNode.RestoreDefaultParameters();

        await rig.ResetAsync(NmtCommand.ResetNode);

        DeviceTypeOf(rig.SlaveNode, 0x6FFF).Should().Be(Profile408, "1011h load returns the chain to what Attach composed");
    }

    // -----------------------------------------------------------------------------------------
    // 1000h composed by the node is the node's power-on value: restorable like the chain.
    // -----------------------------------------------------------------------------------------

    private const uint Overwritten = 0x00070123;

    [Theory]
    [InlineData(NmtCommand.ResetNode)]
    [InlineData(NmtCommand.ResetCommunication)]
    public async Task A_Reset_Restores_The_Composed_1000h_After_It_Was_Overwritten(NmtCommand reset)
    {
        using var rig = new ResetRig(Typed("one", (0, Profile401)));
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x1000, 0x00, Overwritten);

        await rig.ResetAsync(reset);

        DeviceTypeOf(rig.SlaveNode).Should().Be(Profile401,
            "the node composed 1000h, so the composed word is its power-on value, and 1000h lies in the area both resets restore");
    }

    [Fact]
    public async Task A_Stored_Overwrite_Of_The_Composed_1000h_Survives_Reset_Node()
    {
        // The rule of every restorable object: 1010h stores what the dictionary holds at the "save",
        // and Reset Node returns to the last stored values.
        using var rig = new ResetRig(Typed("one", (0, Profile401)));
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x1000, 0x00, Overwritten);
        rig.SlaveNode.StoreParameters();
        rig.Od.WriteUnsigned(0x1000, 0x00, 0);

        await rig.ResetAsync(NmtCommand.ResetNode);

        DeviceTypeOf(rig.SlaveNode).Should().Be(Overwritten, "the save stored it");
    }

    [Fact]
    public async Task Restoring_Defaults_Returns_1000h_To_The_Composed_Word()
    {
        using var rig = new ResetRig(Typed("one", (0, Profile401)));
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x1000, 0x00, Overwritten);
        rig.SlaveNode.StoreParameters();
        rig.SlaveNode.RestoreDefaultParameters();

        await rig.ResetAsync(NmtCommand.ResetNode);

        DeviceTypeOf(rig.SlaveNode).Should().Be(Profile401, "1011h load returns 1000h to what Attach composed");
    }

    [Fact]
    public async Task An_Applications_Own_1000h_Stays_Across_A_Reset()
    {
        // No SetDeviceType: the application put the word there itself, so the node has no power-on
        // value for 1000h, as without applications (FR-CO-019).
        const uint Own = 0x00030191;
        var app = new RecordingApplication("writer", CanOpenIndexRange.ForLogicalDevice(0))
        {
            OnAttach = host => host.ObjectDictionary.WriteUnsigned(0x1000, 0x00, Own),
        };
        using var rig = new ResetRig(app);
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x1000, 0x00, Overwritten);

        await rig.ResetAsync(NmtCommand.ResetNode);

        DeviceTypeOf(rig.SlaveNode).Should().Be(Overwritten, "the node did not compose 1000h, so it does not restore it");
    }

    [Fact]
    public void A_Chain_Object_The_Application_Declared_Itself_Fails_The_Open()
    {
        var app = Typed("clash", (0, Profile401), (1, Profile408));
        var set = app.OnAttach!;
        app.OnAttach = host =>
        {
            host.ObjectDictionary.AddU32(0x6FFF, 0x00, 0x00000000, OdAccess.ReadOnly);
            set(host);
        };

        var failure = OptionsRefused(With(app));

        failure.Message.Should().Contain("6FFF").And.Contain("clash").And.Contain("SetDeviceType");
    }

    [Fact]
    public void An_Object_At_67FFh_Is_Left_Alone_When_There_Is_One_Logical_Device()
    {
        using var bus = Open(NewSession(), 1);
        var app = Typed("one", (0, Profile401));
        var set = app.OnAttach!;
        app.OnAttach = host =>
        {
            host.ObjectDictionary.AddU32(0x67FF, 0x00, 0x00000042, OdAccess.ReadOnly);
            set(host);
        };

        using var node = OpenNode(bus, With(app));

        DeviceTypeOf(node, 0x67FF).Should().Be(0x42u, "the node declares 67FFh only for a chain");
    }

    [Fact]
    public void A_Description_Keeps_Its_Own_1000h()
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("one", (0, Profile401))),
            description: DescriptionWith6000(deviceType: 0x00030191));

        DeviceTypeOf(node).Should().Be(0x00030191u, "the file wins; its profile number is the application's");
    }

    [Fact]
    public void A_Description_Of_Another_Profile_Fails_The_Open()
    {
        var failure = OptionsRefused(With(Typed("one", (0, Profile401))),
            DescriptionWith6000(deviceType: 0x00000192));

        failure.Message.Should().Contain("0x1000").And.Contain("402").And.Contain("401").And.Contain("one");
    }

    [Fact]
    public void A_Described_Chain_Keeps_Its_Own_Words()
    {
        using var bus = Open(NewSession(), 1);
        var description = DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, 0x00020198));

        using var node = OpenNode(bus, With(Typed("two", (0, Profile401), (1, Profile408))), description: description);

        DeviceTypeOf(node).Should().Be(0xFFFF0191u);
        DeviceTypeOf(node, 0x67FF).Should().Be(0x00050191u);
        DeviceTypeOf(node, 0x6FFF).Should().Be(0x00020198u);
    }

    [Fact]
    public void A_Described_Chain_Object_Of_Another_Profile_Fails_The_Open()
    {
        var description = DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, 0x00020199));

        var failure = OptionsRefused(With(Typed("two", (0, Profile401), (1, Profile408))), description);

        failure.Message.Should().Contain("6FFF").And.Contain("409").And.Contain("408");
    }

    [Fact]
    public void A_Described_Device_Type_Object_That_Is_Not_Unsigned32_Fails_The_Open()
    {
        // 67FFh described as an UNSIGNED16: there is no device type to compare with the one set.
        var eds = EdsWith6000(0x00000191, (0x67FF, 0x0191)).Replace(
            "[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x00000191",
            "[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0006\nAccessType=ro\nDefaultValue=0x0191");

        var failure = OptionsRefused(With(Typed("one", (0, Profile401))), CanOpenDeviceDescription.ParseEds(eds));

        failure.Message.Should().Contain("0x67FF").And.Contain("UNSIGNED32").And.Contain("one");
    }

    [Fact]
    public void All_Eight_Logical_Devices_Make_A_Full_Chain()
    {
        using var bus = Open(NewSession(), 1);
        var types = Enumerable.Range(0, 8).Select(device => (device, 0x00010000u | (uint)(0x0191 + device))).ToArray();

        using var node = OpenNode(bus, With(Typed("all", types)));

        DeviceTypeOf(node).Should().Be(0xFFFF0191u);
        for (int device = 0; device < 8; device++)
            DeviceTypeOf(node, (ushort)(0x67FF + device * 0x800)).Should().Be(types[device].Item2);
    }

    [Fact]
    public void A_Described_Chain_Object_Is_Checked_For_A_Single_Device_Too()
    {
        var description = DescriptionWith6000(0x00000191, (0x67FF, 0x00000192));

        OptionsRefused(With(Typed("one", (0, Profile401))), description)
            .Message.Should().Contain("67FF").And.Contain("402").And.Contain("401");
    }

    // -----------------------------------------------------------------------------------------
    // The topology 1000h announces, checked before the boot-up (#551, CiA 301 v4.2.0 §7.5.2.1).
    // -----------------------------------------------------------------------------------------

    [Fact] // #551
    public void A_Described_Single_Device_1000h_Over_Two_Logical_Devices_Fails_The_Open()
    {
        // The file's 1000h says one logical device; the applications attach two. A reader of 1000h
        // would stop there and never see logical device 1 in 6FFFh.
        var failure = OptionsRefused(With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            DescriptionWith6000(deviceType: Profile401));

        failure.Message.Should().Contain("0x1000").And.Contain("0x000F0191").And.Contain("FFFF")
            .And.Contain("logical device 1").And.Contain("valve");
    }

    [Fact] // #551
    public void A_Described_1000h_That_Announces_A_Chain_The_Node_Does_Not_Have_Fails_The_Open()
    {
        // FFFFh above the profile, one logical device and no 67FFh: a reader takes 1000h's word for the
        // device's own, FFFFh and all.
        var failure = OptionsRefused(With(Typed("io", (0, Profile401))), DescriptionWith6000(deviceType: 0xFFFF0191));

        failure.Message.Should().Contain("0x1000").And.Contain("0x67FF").And.Contain("io");
    }

    [Fact] // #551, #575
    public void A_Described_Chain_Announcement_Over_One_Device_And_Only_A_Described_67FFh_Fails_The_Open()
    {
        // FFFFh says several logical devices (CiA 301 v4.2.0 §7.5.2.1); a reader finds the first at
        // 67FFh and the peer's "object does not exist" at 6FFFh, and calls the chain contradictory (#552).
        var failure = OptionsRefused(With(Typed("io", (0, Profile401))),
            DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191)));

        failure.Message.Should().Contain("0x1000").And.Contain("0xFFFF0191").And.Contain("0x6FFF").And.Contain("io");
    }

    [Fact] // #575
    public void A_Described_Chain_Announcement_Over_One_Device_Is_Kept_When_The_File_Declares_A_Second()
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("io", (0, Profile401))),
            description: DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, 0x00020198)));

        DeviceTypeOf(node).Should().Be(0xFFFF0191u, "the file's chain has two devices: a reader finds both");
        DeviceTypeOf(node, 0x67FF).Should().Be(0x00050191u);
        DeviceTypeOf(node, 0x6FFF).Should().Be(0x00020198u);
    }

    /// <summary>One logical device of profile 401 whose application declares <c>67FFh</c> itself, under a
    /// described <c>1000h</c> of <c>FFFF0191h</c>: the reader of <c>1000h</c> takes that object for the device's
    /// type.</summary>
    private static CanOpenNodeOptions OneDeviceDeclaring67FF(uint word, OdAccess access)
    {
        var app = Typed("io", (0, Profile401));
        var set = app.OnAttach!;
        app.OnAttach = host =>
        {
            host.ObjectDictionary.AddU32(0x67FF, 0x00, word, access);
            set(host);
        };
        return With(app);
    }

    [Fact] // #551, review
    public void An_Application_Declared_67FFh_Behind_A_Described_Chain_Announcement_Is_Checked_For_Its_Profile()
    {
        var failure = OptionsRefused(OneDeviceDeclaring67FF(0x00000192, OdAccess.ReadOnly), DescriptionWith6000(deviceType: 0xFFFF0191));

        failure.Message.Should().Contain("0x67FF").And.Contain("402").And.Contain("401").And.Contain("io");
    }

    [Fact] // #551, review
    public void An_Application_Declared_67FFh_Behind_A_Described_Chain_Announcement_Must_Be_Read_Only()
    {
        var failure = OptionsRefused(OneDeviceDeclaring67FF(Profile401, OdAccess.ReadWrite), DescriptionWith6000(deviceType: 0xFFFF0191));

        failure.Message.Should().Contain("0x67FF").And.Contain("read-only").And.Contain("io");
    }

    [Fact] // #551, review; #575
    public void A_Valid_Application_Declared_67FFh_Behind_A_Described_Chain_Announcement_Needs_A_Second_Device()
    {
        // Valid as a device type, but the only one: FFFFh announces several (#575).
        var failure = OptionsRefused(OneDeviceDeclaring67FF(Profile401, OdAccess.ReadOnly),
            DescriptionWith6000(deviceType: 0xFFFF0191));
        failure.Message.Should().Contain("0x6FFF").And.Contain("io");

        // With a second device in the file it is kept, and checked as before.
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, OneDeviceDeclaring67FF(Profile401, OdAccess.ReadOnly),
            description: DescriptionWith6000(0xFFFF0191, (0x6FFF, Profile408)));
        DeviceTypeOf(node, 0x67FF).Should().Be(Profile401);
    }

    [Fact] // #551
    public void A_Single_Device_Word_That_Announces_Further_Devices_Fails_The_Open()
    {
        var failure = OptionsRefused(With(Typed("lonely", (0, 0xFFFF0198))));

        failure.Message.Should().Contain("lonely").And.Contain("0xFFFF0198").And.Contain("FFFF");
    }

    [Theory] // #551
    [InlineData(0x00010000u)]
    [InlineData(0x80000000u)]
    public void An_Unprofiled_Single_Device_With_Additional_Information_Fails_The_Open(uint word)
    {
        var failure = OptionsRefused(With(Typed("plain", (0, word))));

        failure.Message.Should().Contain("plain").And.Contain($"0x{word:X8}").And.Contain("0000h");
    }

    [Fact] // #551
    public void An_Unprofiled_Logical_Device_In_A_Chain_With_Additional_Information_Fails_The_Open()
    {
        var failure = OptionsRefused(With(Typed("two", (0, Profile401), (1, 0x00010000))));

        failure.Message.Should().Contain("two").And.Contain("logical device 1").And.Contain("0x00010000");
    }

    [Theory] // #551
    [InlineData(0x00000000u)]
    [InlineData(0x000F0191u)]
    [InlineData(0xFF8F0191u)]
    public void A_Valid_Single_Device_Word_Is_1000h_As_Set(uint word)
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("one", (0, word))));

        DeviceTypeOf(node).Should().Be(word, "the profile's capability bits are its own (§7.5.2.1)");
    }

    [Fact] // #551
    public async Task A_Described_Chain_Of_Two_Devices_Reads_Back_Over_The_Bus()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        // 77FFh too, so that the slave itself ends the chain ("object does not exist").
        master.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF, 0x77FF));
        var bootups = new BootupWatch(master, Slave);
        using var slave = OpenNode(busB, With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            description: DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191)));
        await bootups.First;

        var result = await CanOpenLogicalDevices.ReadAsync(master, Slave).WithTimeoutAsync(ShortTimeout);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.DeviceType.Value.Should().Be(0xFFFF0191u);
        result.Devices.Select(d => (d.Index, d.DeviceType.Value)).Should().Equal((0, 0x00050191u), (1, Profile408));
    }

    // -----------------------------------------------------------------------------------------
    // A described device-type object: its declaration as well as its value (#553).
    // -----------------------------------------------------------------------------------------

    /// <summary>The two-device description of <see cref="A_Described_Chain_Keeps_Its_Own_Words"/>,
    /// with <c>67FFh</c> declared with <paramref name="dataType"/> and <paramref name="access"/> instead
    /// of UNSIGNED32 and <c>ro</c>; the word, <c>000F0191h</c>, still fits in four bytes.</summary>
    private static CanOpenDeviceDescription ChainWith67FFDeclaredAs(string dataType, string access)
    {
        var eds = EdsWith6000(0xFFFF0191, (0x67FF, Profile401));
        const string Declared = "[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\n";
        eds.Should().Contain(Declared);
        return CanOpenDeviceDescription.ParseEds(eds.Replace(Declared,
            $"[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType={dataType}\nAccessType={access}\n"));
    }

    private static CanOpenNodeOptions TwoDevices() => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408)));

    [Fact] // #553
    public void A_Described_Device_Type_Object_Of_Another_Type_Of_The_Same_Width_Fails_The_Open()
    {
        var failure = OptionsRefused(TwoDevices(), ChainWith67FFDeclaredAs("0x0004", "ro"));

        failure.Message.Should().Contain("0x67FF").And.Contain("UNSIGNED32").And.Contain("io");
    }

    [Theory] // #553
    [InlineData("rw")]
    [InlineData("wo")]
    [InlineData("rww")]
    [InlineData("rwr")]
    public void A_Writable_Described_Device_Type_Object_Fails_The_Open(string access)
    {
        var failure = OptionsRefused(TwoDevices(), ChainWith67FFDeclaredAs("0x0007", access));

        failure.Message.Should().Contain("0x67FF").And.Contain("read-only").And.Contain("io");
    }

    [Fact] // #553
    public void A_Described_Device_Type_Object_Without_Sub_Index_0_Fails_The_Open()
    {
        // 67FFh described as a RECORD whose only entry is sub-index 1: there is no device type to read.
        var eds = EdsWith6000(0xFFFF0191, (0x67FF, Profile401));
        const string Declared = "[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x000F0191\nPDOMapping=0\n";
        eds.Should().Contain(Declared);
        var description = CanOpenDeviceDescription.ParseEds(eds.Replace(Declared,
            "[67FF]\nParameterName=Device type\nObjectType=0x9\nSubNumber=1\n" +
            "[67FFsub1]\nParameterName=Word\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x000F0191\nPDOMapping=0\n"));

        var failure = OptionsRefused(TwoDevices(), description);

        failure.Message.Should().Contain("0x67FF").And.Contain("UNSIGNED32 at sub-index 0").And.Contain("io");
    }

    [Theory] // #553
    [InlineData("ro")]
    [InlineData("const")]
    public void A_Read_Only_Unsigned32_Device_Type_Object_Is_Accepted(string access)
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, TwoDevices(), description: ChainWith67FFDeclaredAs("0x0007", access));

        node.ObjectDictionary.TryGet(0x67FF, 0x00, out var entry).Should().BeTrue();
        entry.DataType.Should().Be(OdDataType.Unsigned32);
        entry.Access.Should().Be(OdAccess.ReadOnly);
        DeviceTypeOf(node, 0x67FF).Should().Be(Profile401);
    }

    [Fact]
    public void A_Partly_Described_Chain_Declares_The_Rest()
    {
        using var bus = Open(NewSession(), 1);
        var description = DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191));

        using var node = OpenNode(bus, With(Typed("two", (0, Profile401), (1, Profile408))), description: description);

        DeviceTypeOf(node, 0x67FF).Should().Be(0x00050191u, "described: the file's word");
        DeviceTypeOf(node, 0x6FFF).Should().Be(Profile408, "not described: the application's word");
    }

    // -----------------------------------------------------------------------------------------
    // The node and the reader agree on what a chain is (#575, #552).
    // -----------------------------------------------------------------------------------------

    /// <summary>A copy of <see cref="EdsWith6000"/> in which the described device-type object
    /// <paramref name="index"/> is declared with <paramref name="dataType"/>, <paramref name="access"/> and
    /// <paramref name="value"/> instead of a read-only UNSIGNED32.</summary>
    private static CanOpenDeviceDescription ChainWithObjectDeclaredAs(ushort index, string dataType, string access,
        string value, uint deviceType, params (ushort Index, uint Word)[] deviceTypeObjects)
    {
        var eds = EdsWith6000(deviceType, deviceTypeObjects);
        var word = deviceTypeObjects.Single(o => o.Index == index).Word;
        var declared = $"[{index:X4}]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x{word:X8}\n";
        eds.Should().Contain(declared);
        return CanOpenDeviceDescription.ParseEds(eds.Replace(declared,
            $"[{index:X4}]\nParameterName=Device type\nObjectType=0x7\nDataType={dataType}\nAccessType={access}\nDefaultValue={value}\n"));
    }

    /// <summary>Eight logical devices, as <see cref="All_Eight_Logical_Devices_Make_A_Full_Chain"/> sets them:
    /// device 0 of profile 401, so that the composed <c>1000h</c> agrees with <c>67FFh</c>.</summary>
    private static readonly (int Device, uint Word)[] EightDevices =
        Enumerable.Range(0, 8).Select(device => (device, 0x00010000u | (uint)(0x0191 + device))).ToArray();

    /// <summary>A node configuration and what follows from it: the open fails with a message holding
    /// <see cref="Refusal"/>, or the node opens and <see cref="CanOpenLogicalDevices.ReadAsync"/> reads
    /// <see cref="Devices"/> and ends as <see cref="End"/>.</summary>
    public sealed record NodeChainCase(
        string Name,
        Func<CanOpenNodeOptions> Options,
        Func<CanOpenDeviceDescription?> Description,
        string[]? Refusal,
        CanOpenLogicalDeviceReadEnd End = CanOpenLogicalDeviceReadEnd.Complete,
        uint[]? Devices = null)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// Hand-picked node configurations, at least one for each row of the reader's table (remarks of
    /// <see cref="CanOpenLogicalDevices.ReadAsync"/>) that a node with applications can be configured
    /// for, and the neighbours of the checks that keep the contradictory rows off the wire. The list is
    /// not derived from the table: a new row there needs its configuration added here. Every
    /// configuration either fails to open or publishes a chain the reader reads as complete: none
    /// publishes what the reader calls contradictory. The other rows are not the node's to produce: a
    /// gate refusal is the reading client's description, and an abort other than "object does not
    /// exist" or a timeout is not a property of the dictionary.
    /// </summary>
    public static TheoryData<NodeChainCase> NodeChains => new()
    {
        new NodeChainCase("no FFFFh: one device, 1000h composed",
            () => With(Typed("io", (0, Profile401))), () => null,
            null, CanOpenLogicalDeviceReadEnd.Complete, [Profile401]),
        new NodeChainCase("no FFFFh: a described single-device 1000h; the file's 67FFh is not read",
            () => With(Typed("io", (0, Profile401))), () => DescriptionWith6000(0x00030191, (0x67FF, 0x00050191)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [0x00030191]),
        new NodeChainCase("FFFFh: two devices composed",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))), () => null,
            null, CanOpenLogicalDeviceReadEnd.Complete, [Profile401, Profile408]),
        new NodeChainCase("FFFFh: a described chain of two devices",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            () => DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, 0x00020198)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [0x00050191, 0x00020198]),
        new NodeChainCase("FFFFh: one device attached, the file declares the second",
            () => With(Typed("io", (0, Profile401))),
            () => DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, 0x00020198)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [0x00050191, 0x00020198]),
        new NodeChainCase("FFFFh: two devices attached, the file declares a valid third",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            () => DescriptionWith6000(0xFFFF0191, (0x77FF, 0x00010192)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [Profile401, Profile408, 0x00010192]),
        new NodeChainCase("FFFFh: eight devices, the full chain",
            () => With(Typed("all", EightDevices)), () => null,
            null, CanOpenLogicalDeviceReadEnd.Complete, EightDevices.Select(d => d.Word).ToArray()),
        new NodeChainCase("#552 (1): FFFFh without 67FFh",
            () => With(Typed("io", (0, Profile401))), () => DescriptionWith6000(deviceType: 0xFFFF0191),
            ["0x1000", "0x67FF", "io"]),
        new NodeChainCase("#552 (1): FFFFh, a described 67FFh and nothing at 6FFFh",
            () => With(Typed("io", (0, Profile401))), () => DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191)),
            ["0x1000", "0x6FFF", "io"]),
        new NodeChainCase("#552 (1): FFFFh, an application-declared 67FFh and nothing at 6FFFh",
            () => OneDeviceDeclaring67FF(Profile401, OdAccess.ReadOnly), () => DescriptionWith6000(deviceType: 0xFFFF0191),
            ["0x1000", "0x6FFF", "io"]),
        new NodeChainCase("#552 (2): a described 67FFh of another profile than 1000h",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            () => DescriptionWith6000(0xFFFF0191, (0x67FF, Profile408), (0x6FFF, Profile408)),
            ["0x67FF", "408", "401"]),
        new NodeChainCase("wrong length: one device, the file's second device-type object is an UNSIGNED16",
            () => With(Typed("io", (0, Profile401))),
            () => ChainWithObjectDeclaredAs(0x6FFF, "0x0006", "ro", "0x0198", 0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, Profile408)),
            ["0x6FFF", "UNSIGNED32"]),
        new NodeChainCase("wrong length: two devices, the file's third device-type object is an UNSIGNED16",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            () => ChainWithObjectDeclaredAs(0x77FF, "0x0006", "ro", "0x0198", 0xFFFF0191, (0x67FF, Profile401), (0x6FFF, Profile408), (0x77FF, Profile408)),
            ["0x77FF", "UNSIGNED32"]),
        new NodeChainCase("no FFFFh: an UNSIGNED16 at 6FFFh is not read and not refused",
            () => With(Typed("io", (0, Profile401))),
            () => ChainWithObjectDeclaredAs(0x6FFF, "0x0006", "ro", "0x0198", 0x00030191, (0x6FFF, Profile408)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [0x00030191]),
        new NodeChainCase("FFFFh: an UNSIGNED16 behind a gap in the chain is not read and not refused",
            () => With(Typed("io", (0, Profile401)), Typed("valve", (1, Profile408))),
            () => ChainWithObjectDeclaredAs(0x7FFF, "0x0006", "ro", "0x0198", 0xFFFF0191, (0x7FFF, Profile408)),
            null, CanOpenLogicalDeviceReadEnd.Complete, [Profile401, Profile408]),
    };

    [Theory] // #575, #552, FR-CO-063
    [MemberData(nameof(NodeChains))]
    public async Task The_Node_Publishes_No_Chain_The_Reader_Calls_Contradictory(NodeChainCase chain)
    {
        if (chain.Refusal is { } refusal)
        {
            var failure = OptionsRefused(chain.Options(), chain.Description());
            foreach (var part in refusal) failure.Message.Should().Contain(part);
            return;
        }

        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        // Every device-type object: the peer, not the gate, ends each chain.
        master.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(
            0x1000, 0x67FF, 0x6FFF, 0x77FF, 0x7FFF, 0x87FF, 0x8FFF, 0x97FF, 0x9FFF));
        var bootups = new BootupWatch(master, Slave);
        using var slave = OpenNode(busB, chain.Options(), description: chain.Description());
        await bootups.First;

        var result = await CanOpenLogicalDevices.ReadAsync(master, Slave).WithTimeoutAsync(ShortTimeout);

        result.End.Should().Be(chain.End);
        result.Failure.Should().BeNull();
        result.Devices.Select(d => d.DeviceType.Value).Should().Equal(chain.Devices);
    }

    /// <summary>A device-type object behind the attached devices, which a reader of an announced chain
    /// asks for like any other, is a read-only UNSIGNED32 as an attached device's is (#553, #575).</summary>
    [Theory] // #575
    [InlineData("0x0007", "rw")]
    [InlineData("0x0004", "ro")]
    public void A_Device_Type_Object_Behind_The_Attached_Devices_Is_A_Read_Only_Unsigned32(string dataType, string access)
    {
        var failure = OptionsRefused(With(Typed("io", (0, Profile401))),
            ChainWithObjectDeclaredAs(0x6FFF, dataType, access, "0x00010198", 0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, Profile408)));

        failure.Message.Should().Contain("0x6FFF").And.Contain("logical device 1").And.Contain("The device description");
    }

    /// <summary>A described device-type object behind the attached devices that has no sub-index 0, a
    /// RECORD with only sub-index 1, holds no device type a reader could read (#575, as #553 for an
    /// attached device).</summary>
    [Fact] // #575
    public void A_Device_Type_Object_Behind_The_Attached_Devices_Without_Sub_Index_0_Fails_The_Open()
    {
        var eds = EdsWith6000(0xFFFF0191, (0x67FF, 0x00050191), (0x6FFF, Profile408));
        const string Declared = "[6FFF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x00010198\nPDOMapping=0\n";
        eds.Should().Contain(Declared);
        var description = CanOpenDeviceDescription.ParseEds(eds.Replace(Declared,
            "[6FFF]\nParameterName=Device type\nObjectType=0x9\nSubNumber=1\n" +
            "[6FFFsub1]\nParameterName=Word\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x00010198\nPDOMapping=0\n"));

        var failure = OptionsRefused(With(Typed("io", (0, Profile401))), description);

        failure.Message.Should().Contain("0x6FFF").And.Contain("without sub-index 0").And.Contain("logical device 1");
    }

    /// <summary>A device-type object behind the attached devices that an application declared itself is
    /// checked like a described one: a reader of the announced chain asks for it all the same (#575).</summary>
    [Fact] // #575
    public void An_Application_Declared_Device_Type_Object_Behind_The_Attached_Devices_Is_Checked_Too()
    {
        var app = Typed("io", (0, Profile401));
        var set = app.OnAttach!;
        app.OnAttach = host =>
        {
            host.ObjectDictionary.AddU32(0x6FFF, 0x00, Profile408, OdAccess.ReadWrite);
            set(host);
        };

        var failure = OptionsRefused(With(app), DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191)));

        failure.Message.Should().Contain("An application").And.Contain("0x6FFF").And.Contain("ReadWrite");
    }

    /// <summary>A profile device built from a configuration sets a word without <c>FFFFh</c> (CiA 401-1
    /// §6.2.1, CiA 408 Table 55), so a node of one profile device publishes no chain, and a node of two
    /// publishes both devices (#575).</summary>
    [Fact] // #575
    public void Profile_Devices_From_A_Configuration_Announce_Exactly_The_Devices_They_Are()
    {
        using var busA = Open(NewSession(), 1);
        using var busB = Open(NewSession(), 1);
        using var busC = Open(NewSession(), 1);
        var io = new Cia401Device(new Cia401Configuration { DigitalInputs = 8 });
        using var single = OpenNode(busA, Cia401Device.AddTo(new CanOpenNodeOptions(), io));
        var valve = new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.PQControl },
        });
        using var singleValve = OpenNode(busB, Cia408Device.AddTo(new CanOpenNodeOptions(), valve));
        var mixedIo = new Cia401Device(new Cia401Configuration { DigitalInputs = 8 });
        var mixedValve = new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.PQControl },
            LogicalDevice = 1,
        });
        using var mixed = OpenNode(busC, Cia408Device.AddTo(Cia401Device.AddTo(new CanOpenNodeOptions(), mixedIo), mixedValve));

        new CanOpenDeviceType(DeviceTypeOf(single)).HasFurtherLogicalDevices.Should().BeFalse();
        single.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse();
        new CanOpenDeviceType(DeviceTypeOf(singleValve)).HasFurtherLogicalDevices.Should().BeFalse();
        singleValve.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse();
        DeviceTypeOf(mixed).Should().Be(0xFFFF0191u);
        mixed.ObjectDictionary.ContainsIndex(0x67FF).Should().BeTrue();
        mixed.ObjectDictionary.ContainsIndex(0x6FFF).Should().BeTrue();
        mixed.ObjectDictionary.ContainsIndex(0x77FF).Should().BeFalse();
    }
}
