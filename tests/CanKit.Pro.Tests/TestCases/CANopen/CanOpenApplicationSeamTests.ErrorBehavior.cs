using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// The error behaviour record a profile lays out (#559): the CiA 408 v2.2.0 record (§8.6.8,
// Table 56) is :00 = 02h, communication error at :01 and internal device error at :02, both 01h.
public partial class CanOpenApplicationSeamTests
{
    private static readonly CanOpenErrorBehaviorLayout Layout408 = new() { CommunicationErrorDefault = 1, AdditionalDefaults = new byte[] { 1 } };

    private static RecordingApplication LayingOut(CanOpenErrorBehaviorLayout layout, string name = "layout", bool monitoring = true)
        => new(name)
        {
            Requirements = new CanOpenApplicationRequirements { CommunicationFailureMonitoring = monitoring, ErrorBehavior = layout },
        };

    private static byte UploadedU8(byte[] response)
    {
        response[0].Should().Be(0x4F, "an expedited upload of one byte");
        return response[4];
    }

    /// <summary>A description that declares <c>1029h</c> with sub-index 00h = <paramref name="count"/>
    /// and the given classes, each UNSIGNED8 rw.</summary>
    private static CanOpenDeviceDescription DescriptionWith1029(byte count, params (byte Subindex, byte Value)[] classes)
    {
        var eds = EdsWith6000();
        var record = "[1029]\nParameterName=Error behavior\nObjectType=0x8\n" + $"SubNumber={classes.Length + 1}\n" +
            $"[1029sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=const\nDefaultValue={count}\nPDOMapping=0\n" +
            string.Concat(classes.Select(c =>
                $"[1029sub{c.Subindex:X}]\nParameterName=Class {c.Subindex}\nObjectType=0x7\nDataType=0x0005\nAccessType=rw\nDefaultValue={c.Value}\nPDOMapping=0\n"));
        eds = eds.Replace("[OptionalObjects]\nSupportedObjects=0\n", "[OptionalObjects]\nSupportedObjects=1\n1=0x1029\n") + record;
        return CanOpenDeviceDescription.ParseEds(eds);
    }

    // -----------------------------------------------------------------------------------------
    // The record.
    // -----------------------------------------------------------------------------------------

    [Fact] // Review Focus 3, #559
    public async Task The_408_Layout_Is_The_Error_Behavior_Record_Over_Sdo()
    {
        var app = LayingOut(Layout408);
        using var rig = new EntryRig(WithErrorBehavior(app));

        var highest = await rig.Wire.UploadAsync(0x1029, 0x00);
        var communication = await rig.Wire.UploadAsync(0x1029, 0x01);
        var internalDevice = await rig.Wire.UploadAsync(0x1029, 0x02);
        var tooHigh = await rig.Wire.DownloadU8Async(0x1029, 0x02, 0x03);
        var stop = await rig.Wire.DownloadU8Async(0x1029, 0x02, 0x02);
        var count = await rig.Wire.DownloadU8Async(0x1029, 0x00, 0x03);

        UploadedU8(highest).Should().Be(0x02);
        UploadedU8(communication).Should().Be(0x01);
        UploadedU8(internalDevice).Should().Be(0x01);
        AbortCode(tooHigh).Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "an error class takes 00h to 02h");
        stop[0].Should().Be(0x60);
        AbortCode(count).Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
        rig.Od.ReadUnsigned(0x1029, 0x02).Should().Be(2u);
        FluentActions.Invoking(() => rig.Od.WriteUnsigned(0x1029, 0x02, 0x03))
            .Should().Throw<ArgumentException>().WithMessage("*06090030*", "a local write is held to the same range");
    }

    [Fact]
    public async Task Without_A_Layout_The_Record_Is_The_Generic_One()
    {
        var app = Watcher();
        using var rig = new EntryRig(WithErrorBehavior(app));

        UploadedU8(await rig.Wire.UploadAsync(0x1029, 0x00)).Should().Be(0x01);
        UploadedU8(await rig.Wire.UploadAsync(0x1029, 0x01)).Should().Be(0x00);
        AbortCode(await rig.Wire.UploadAsync(0x1029, 0x02)).Should().Be((uint)SdoAbortCode.SubIndexDoesNotExist);
    }

    [Fact]
    public async Task The_Node_Acts_On_A_Communication_Error_By_Sub_Index_01h()
    {
        var app = LayingOut(Layout408);
        using var watch = new ProducerWatchRig(app);
        watch.Rig.Od.WriteUnsigned(0x1029, 0x01, 0x02);
        watch.Rig.Od.WriteUnsigned(0x1029, 0x02, 0x00);
        await watch.Rig.StartAsync();
        await watch.BeatAsync();

        watch.Silence();

        watch.Rig.Node.State.Should().Be(NmtState.Stopped, "1029h:01 = 02h; 1029h:02 is the profile's class");
    }

    [Fact]
    public async Task The_Laid_Out_Classes_Are_Stored_And_Restored_With_The_Record()
    {
        var app = LayingOut(Layout408);
        using var rig = new EntryRig(WithErrorBehavior(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);

        rig.Od.WriteUnsigned(0x1029, 0x02, 0x02);
        rig.Node.StoreParameters();
        rig.Od.WriteUnsigned(0x1029, 0x02, 0x00);
        await ResetFromWireAsync(rig, NmtCommand.ResetCommunication);
        var stored = rig.Od.ReadUnsigned(0x1029, 0x02);
        rig.Node.RestoreDefaultParameters();
        await ResetFromWireAsync(rig, NmtCommand.ResetNode);

        stored.Should().Be(2u, "1010h stored the class, and the reset brought it back");
        rig.Od.ReadUnsigned(0x1029, 0x02).Should().Be(1u, "1011h returns it to the layout's default");
        rig.Od.ReadUnsigned(0x1029, 0x00).Should().Be(2u);
    }

    // -----------------------------------------------------------------------------------------
    // What the open checks.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_Layout_Without_ErrorBehavior_Fails_The_Open()
    {
        var app = LayingOut(Layout408, "layout", monitoring: false);

        OpenFails(With(app)).Message.Should().Contain("layout").And.Contain("EnableErrorBehavior").And.Contain("1029h");
    }

    [Fact]
    public void Two_Different_Layouts_Fail_The_Open()
    {
        var first = LayingOut(Layout408, "first");
        var second = LayingOut(new CanOpenErrorBehaviorLayout { CommunicationErrorDefault = 1, AdditionalDefaults = new byte[] { 0 } }, "second");

        OpenFails(WithErrorBehavior(first, second)).Message.Should().Contain("first").And.Contain("second").And.Contain("1029h");
    }

    [Fact]
    public async Task Two_Equal_Layouts_Share_The_Record()
    {
        var first = LayingOut(Layout408, "first");
        var second = LayingOut(new CanOpenErrorBehaviorLayout { CommunicationErrorDefault = 1, AdditionalDefaults = new List<byte> { 1 } }, "second");
        using var rig = new EntryRig(WithErrorBehavior(first, second));

        UploadedU8(await rig.Wire.UploadAsync(0x1029, 0x00)).Should().Be(0x02);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    public void A_Layout_Value_Outside_00h_To_02h_Fails_The_Open(int communication, int additional)
    {
        var app = LayingOut(new CanOpenErrorBehaviorLayout
        {
            CommunicationErrorDefault = (byte)communication,
            AdditionalDefaults = new[] { (byte)additional },
        });

        OpenFails(WithErrorBehavior(app)).Message.Should().Contain("layout").And.Contain("03h");
    }

    [Fact]
    public void A_Layout_With_More_Classes_Than_The_Record_Holds_Fails_The_Open()
    {
        var app = LayingOut(new CanOpenErrorBehaviorLayout { AdditionalDefaults = new byte[254] });

        OpenFails(WithErrorBehavior(app)).Message.Should().Contain("254");
    }

    // -----------------------------------------------------------------------------------------
    // A device description's 1029h.
    // -----------------------------------------------------------------------------------------

    [Fact] // #559
    public void A_Description_That_Lacks_A_Laid_Out_Class_Fails_The_Open()
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        var app = LayingOut(Layout408);

        var failure = Assert.Throws<ArgumentException>(() => new CanOpenNode(service, Slave, WithErrorBehavior(app),
            ownsService: false, description: DescriptionWith1029(1, (1, 1))));

        failure.ParamName.Should().Be("options");
        failure.Message.Should().Contain("02h").And.Contain("layout").And.Contain("1029h");
    }

    [Fact] // #559: "the loader fixes the highest sub-index and omits unsupported entries"
    public void A_Descriptions_1029h_Keeps_Its_Classes_And_Values_When_The_Layout_Covers_Them()
    {
        var app = LayingOut(Layout408);
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, WithErrorBehavior(app), description: DescriptionWith1029(2, (1, 2), (2, 0)));
        Settle(node);

        node.ObjectDictionary.ReadUnsigned(0x1029, 0x00).Should().Be(2u);
        node.ObjectDictionary.ReadUnsigned(0x1029, 0x01).Should().Be(2u, "the file's value wins over the layout's default");
        node.ObjectDictionary.ReadUnsigned(0x1029, 0x02).Should().Be(0u, "the file's value wins over the layout's default");
        node.DeviceDescription!.Findings.Should().NotContain(f => f.Index == 0x1029, "the file and the layout agree on the record");
    }

    [Fact]
    public void A_Descriptions_Class_Above_The_Layout_Is_Omitted_As_Before()
    {
        var app = LayingOut(Layout408);
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, WithErrorBehavior(app), description: DescriptionWith1029(3, (1, 1), (2, 1), (3, 1)));

        node.ObjectDictionary.ReadUnsigned(0x1029, 0x00).Should().Be(2u, "sub-index 00h is the layout's");
        node.ObjectDictionary.TryGet(0x1029, 0x03, out _).Should().BeFalse();
        node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        node.DeviceDescription.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 3 && f.Outcome == DeviceDescriptionOutcome.Omitted);
    }

    [Fact]
    public void Without_A_Layout_A_Descriptions_Second_Class_Is_Omitted_As_Before()
    {
        var app = Watcher();
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, WithErrorBehavior(app), description: DescriptionWith1029(2, (1, 1), (2, 1)));

        node.ObjectDictionary.ReadUnsigned(0x1029, 0x00).Should().Be(1u);
        node.ObjectDictionary.TryGet(0x1029, 0x02, out _).Should().BeFalse();
        node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 2 && f.Outcome == DeviceDescriptionOutcome.Omitted);
    }

    // -----------------------------------------------------------------------------------------
    // ErrorBehaviorTarget: CiA 301 v4.2.0 §7.5.2.32, Table 63, against the present state.
    // -----------------------------------------------------------------------------------------

    public static IEnumerable<object?[]> ErrorBehaviorTable() => new[]
    {
        new object?[] { (byte)0x00, NmtState.PreOperational, null },
        new object?[] { (byte)0x00, NmtState.Operational, NmtState.PreOperational },
        new object?[] { (byte)0x00, NmtState.Stopped, null },
        new object?[] { (byte)0x01, NmtState.PreOperational, null },
        new object?[] { (byte)0x01, NmtState.Operational, null },
        new object?[] { (byte)0x01, NmtState.Stopped, null },
        new object?[] { (byte)0x02, NmtState.PreOperational, NmtState.Stopped },
        new object?[] { (byte)0x02, NmtState.Operational, NmtState.Stopped },
        new object?[] { (byte)0x02, NmtState.Stopped, null },
    };

    [Theory]
    [MemberData(nameof(ErrorBehaviorTable))]
    public async Task ErrorBehaviorTarget_Applies_Table_63_To_The_Present_State(byte value, NmtState state, NmtState? expected)
    {
        var app = LayingOut(Layout408);
        using var rig = new EntryRig(WithErrorBehavior(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        rig.Od.WriteUnsigned(0x1029, 0x02, value);
        if (state != NmtState.PreOperational)
        {
            rig.Wire.Transmit(0x000, new[] { (byte)(state == NmtState.Operational ? NmtCommand.Start : NmtCommand.Stop), Slave });
            await WaitUntilAsync(() => rig.Node.State == state, state.ToString());
        }

        NmtState? target = NmtState.Initializing;
        await OnActor(rig.Node, () => target = app.Host!.ErrorBehaviorTarget(0x02));

        target.Should().Be(expected);
    }

    [Fact]
    public async Task ErrorBehaviorTarget_Reads_The_Present_Value_And_Nothing_Where_There_Is_No_Class()
    {
        var app = LayingOut(Layout408);
        using var rig = new EntryRig(WithErrorBehavior(app));
        await rig.StartAsync();
        var targets = new List<NmtState?>();

        rig.Od.WriteUnsigned(0x1029, 0x01, 0x02);
        await OnActor(rig.Node, () => targets.Add(app.Host!.ErrorBehaviorTarget(0x01)));
        rig.Od.WriteUnsigned(0x1029, 0x01, 0x00);
        await OnActor(rig.Node, () => targets.Add(app.Host!.ErrorBehaviorTarget(0x01)));
        await OnActor(rig.Node, () => targets.Add(app.Host!.ErrorBehaviorTarget(0x03)));

        targets.Should().Equal(NmtState.Stopped, NmtState.PreOperational, null);
    }

    [Fact]
    public async Task ErrorBehaviorTarget_Refuses_Sub_Index_00h_And_A_Call_Off_The_Actor()
    {
        var app = LayingOut(Layout408);
        using var rig = new EntryRig(WithErrorBehavior(app));
        Exception? zero = null;

        await OnActor(rig.Node, () => zero = Record.Exception(() => app.Host!.ErrorBehaviorTarget(0x00)));

        zero.Should().BeOfType<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("subindex");
        FluentActions.Invoking(() => app.Host!.ErrorBehaviorTarget(0x01))
            .Should().Throw<InvalidOperationException>().WithMessage("*actor loop*");
    }
}
