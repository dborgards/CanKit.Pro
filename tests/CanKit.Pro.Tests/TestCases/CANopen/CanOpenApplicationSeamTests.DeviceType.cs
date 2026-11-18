using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
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

    [Fact] // #551
    public void A_Described_Chain_Announcement_Over_A_Described_67FFh_Is_Kept_For_One_Device()
    {
        using var bus = Open(NewSession(), 1);

        using var node = OpenNode(bus, With(Typed("io", (0, Profile401))),
            description: DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191)));

        DeviceTypeOf(node).Should().Be(0xFFFF0191u, "the file's chain has its 67FFh: a reader finds the device there");
        DeviceTypeOf(node, 0x67FF).Should().Be(0x00050191u);
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

    [Fact]
    public void A_Partly_Described_Chain_Declares_The_Rest()
    {
        using var bus = Open(NewSession(), 1);
        var description = DescriptionWith6000(0xFFFF0191, (0x67FF, 0x00050191));

        using var node = OpenNode(bus, With(Typed("two", (0, Profile401), (1, Profile408))), description: description);

        DeviceTypeOf(node, 0x67FF).Should().Be(0x00050191u, "described: the file's word");
        DeviceTypeOf(node, 0x6FFF).Should().Be(Profile408, "not described: the application's word");
    }
}
