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
        using var plain = OpenNode(busA, new CanOpenNodeOptions());
        var app = new RecordingApplication("silent", CanOpenIndexRange.ForLogicalDevice(0));

        using var node = OpenNode(busB, With(app));

        DeviceTypeOf(node).Should().Be(DeviceTypeOf(plain));
        node.ObjectDictionary.ContainsIndex(0x67FF).Should().BeFalse();
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
    public void A_Double_Setting_That_The_Application_Swallows_Still_Fails_The_Open()
    {
        var app = new RecordingApplication("careless", CanOpenIndexRange.ForLogicalDevice(0))
        {
            OnAttach = host =>
            {
                host.SetDeviceType(0, new CanOpenDeviceType(Profile401));
                try { host.SetDeviceType(0, new CanOpenDeviceType(Profile408)); }
                catch (ArgumentException) { }
            },
        };

        OptionsRefused(With(app)).Message.Should().Contain("careless");
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
    public async Task The_Device_Type_Survives_Reset_Node()
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
    public void A_Described_Chain_Object_Is_Checked_For_A_Single_Device_Too()
    {
        var description = DescriptionWith6000(0x00000191, (0x67FF, 0x00000192));

        OptionsRefused(With(Typed("one", (0, Profile401))), description)
            .Message.Should().Contain("67FF").And.Contain("402").And.Contain("401");
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
