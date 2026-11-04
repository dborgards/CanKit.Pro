using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// #434, FR-CO-062: the pre-defined error field <c>1003h</c> of CiA 301 §7.5.2.4 and transitions 1
/// and 3 of Figure 37. A node keeps it when <see cref="CanOpenNodeOptions.ErrorHistoryCapacity"/>
/// or its device description asks for it: the newest error at sub-index 01h, the error code in the
/// low 16 bits. Requests travel as raw SDO frames, so no SDO client depends on a clock.
/// </summary>
public partial class CanOpenPdoEngineTests
{
    private static readonly uint SdoRequest = CanOpenCobId.SdoRx(Device);
    private static readonly uint SdoResponse = CanOpenCobId.SdoTx(Device);
    private const byte HistoryUploadInit = 0x40;
    private const byte HistoryBlockUploadInit = 0xA0;
    private const byte HistoryDownloadOneByte = 0x2F;
    private const byte HistoryDownloadFourBytes = 0x23;

    private static ICanOpenNode OpenWithHistory(string session, int capacity, out Wire wire)
    {
        var bus = Open(session, 0);
        wire = new Wire(session, 2);
        return OpenClocked(bus, Device, new ManualTimeSource(), new CanOpenNodeOptions { ErrorHistoryCapacity = capacity });
    }

    private static uint[] History(ICanOpenNode node)
    {
        var od = node.ObjectDictionary;
        return Enumerable.Range(1, (int)od.ReadUnsigned(0x1003, 0)).Select(n => od.ReadUnsigned(0x1003, (byte)n)).ToArray();
    }

    /// <summary>Sends one SDO request and returns the response to it.</summary>
    private static async Task<byte[]> SdoExchangeAsync(Wire wire, byte command, byte subindex, params byte[] rest)
    {
        int before = wire.Payloads(SdoResponse).Length;
        var frame = new byte[8];
        frame[0] = command;
        frame[1] = 0x03;
        frame[2] = 0x10;
        frame[3] = subindex;
        rest.CopyTo(frame, 4);
        wire.Transmit(SdoRequest, frame);
        await wire.WaitForCountAsync(SdoResponse, before + 1);
        return wire.Payloads(SdoResponse)[before];
    }

    private static byte[] HistoryAbort(byte subindex, SdoAbortCode code)
    {
        uint value = (uint)code;
        return new byte[] { 0x80, 0x03, 0x10, subindex, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
    }

    private static CanOpenDeviceDescription EdsWithHistory(string subZeroAccess, string subZeroValue, params string[] entryAccess)
        => CanOpenDeviceDescription.ParseEds(EdsTextWithHistory(subZeroAccess, subZeroValue, entryAccess));

    private static string EdsTextWithHistory(string subZeroAccess, string subZeroValue, params string[] entryAccess)
    {
        var text = File.ReadAllText(CanOpenDeviceDescriptionTests.Fixture("device.eds")).Replace("\r\n", "\n");
        text.Should().Contain("SupportedObjects=12\n");
        var sections = $"[1003]\nParameterName=Pre-defined error field\nObjectType=0x8\nSubNumber={entryAccess.Length + 1}\n\n"
            + $"[1003sub0]\nParameterName=Number of errors\nObjectType=0x7\nDataType=0x0005\nAccessType={subZeroAccess}\nDefaultValue={subZeroValue}\nPDOMapping=0\n\n"
            + string.Concat(entryAccess.Select((access, i) =>
                $"[1003sub{i + 1}]\nParameterName=Standard error field\nObjectType=0x7\nDataType=0x0007\nAccessType={access}\nDefaultValue=0\nPDOMapping=0\n\n"));
        return text.Replace("SupportedObjects=12\n", "SupportedObjects=13\n13=0x1003\n").Replace("[OptionalObjects]\n", sections + "[OptionalObjects]\n");
    }

    private static ICanOpenNode OpenDescribed(string session, CanOpenDeviceDescription description, out Wire wire, int capacity = 0)
    {
        var bus = Open(session, 0);
        wire = new Wire(session, 2);
        return new CanOpenNode(new CanBusService(bus), Device, new CanOpenNodeOptions { ErrorHistoryCapacity = capacity },
            ownsService: true, new ManualTimeSource(), description);
    }

    [Fact]
    public void Without_The_Option_1003h_Is_The_Applications()
    {
        var session = NewSession();
        using var node = OpenClocked(Open(session, 0), Device, new ManualTimeSource());
        node.ObjectDictionary.ContainsIndex(0x1003).Should().BeFalse("the object is optional, and off unless asked for");
        node.ObjectDictionary.AddU32(0x1003, 0x00, 7);
        node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(7u);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(255)]
    public void A_Capacity_Outside_0_To_254_Is_Refused(int capacity)
    {
        var options = new CanOpenNodeOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.WithErrorHistoryCapacity(capacity));
    }

    [Fact]
    public void The_Options_Keep_The_Capacity()
    {
        var options = new CanOpenNodeOptions().WithErrorHistoryCapacity(254);
        options.ErrorHistoryCapacity.Should().Be(254, "§7.5.2.4: the entries end at sub-index FEh");
        options.With(pdoCount: 8).ErrorHistoryCapacity.Should().Be(254);
    }

    [Fact]
    public void The_Object_Has_The_Shape_Of_Section_7_5_2_4()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 3, out var wire);
        using var _w = wire;
        var od = node.ObjectDictionary;
        od.TryGet(0x1003, 0, out var count).Should().BeTrue();
        count.Access.Should().Be(OdAccess.ReadWrite);
        count.PdoMappable.Should().BeFalse();
        od.ReadUnsigned(0x1003, 0).Should().Be(0u);
        foreach (byte sub in new byte[] { 1, 2, 3 })
        {
            od.TryGet(0x1003, sub, out var entry).Should().BeTrue();
            entry.Access.Should().Be(OdAccess.ReadOnly);
            entry.PdoMappable.Should().BeFalse();
        }
        od.TryGet(0x1003, 4, out _).Should().BeFalse("the capacity is 3");
    }

    // §7.5.2.4: the newest at sub-index 01h, older ones moved up; the code low, the additional
    // information high, here the first two manufacturer-specific bytes of the EMCY.
    [Fact]
    public async Task The_Newest_Error_Is_At_Sub_Index_1_With_Its_Manufacturer_Bytes_On_Top()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);

        await node.SendEmcyAsync(0xFF01, 0, new byte[] { 0x34, 0x12, 0x56 });
        History(node).Should().Equal(0x1234_FF01u);
        await node.SendEmcyAsync(0xFF02, 0, new byte[] { 0x78 });
        History(node).Should().Equal(0x0078_FF02u, 0x1234_FF01u);
        await node.SendEmcyAsync(0xFF03, 0);
        History(node).Should().Equal(0x0000_FF03u, 0x0078_FF02u, 0x1234_FF01u);
    }

    [Fact]
    public async Task A_Full_History_Drops_The_Oldest()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);

        foreach (ushort code in new ushort[] { 0xFF01, 0xFF02, 0xFF03 }) await node.SendEmcyAsync(code, 0);
        History(node).Should().Equal(0xFF03u, 0xFF02u);
    }

    // Figure 37: the node's own errors are entered like the application's; 0000h reports that one
    // ended and is no error.
    [Fact]
    public async Task The_Nodes_Own_Error_Is_Entered_And_Its_Reset_Is_Not()
    {
        var session = NewSession();
        var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = OpenClocked(bus, Device, new ManualTimeSource(), new CanOpenNodeOptions { ErrorHistoryCapacity = 4 });
        node.ObjectDictionary.AddU16(0x2000, 0, 0);
        node.ObjectDictionary.AddU16(0x2001, 0, 0);
        node.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        await StartAsync(wire, node);

        await Inject(node, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        History(node).Should().Equal(0x8210u);

        await Inject(node, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0);
        History(node).Should().Equal(0x8210u);

        await node.SendEmcyAsync(ErrorReset, 0);
        History(node).Should().Equal(0x8210u); // the application's 0000h is no error either
    }

    // §7.5.2.4: "signaled via the emergency object". Stopped holds only the most recent EMCY.
    [Fact]
    public async Task Only_The_Emcy_That_Leaves_Stopped_Is_Entered()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(node, NmtState.Stopped);

        await node.SendEmcyAsync(0xFF01, 0);
        await node.SendEmcyAsync(0xFF02, 0);
        History(node).Should().BeEmpty("nothing was signaled yet");

        await StartAsync(wire, node);
        await ExpectEmcyAsync(wire, 1, 0xFF02, 0);
        History(node).Should().Equal(0xFF02u);
    }

    [Fact]
    public async Task An_Emcy_That_Is_Switched_Off_Or_Cancelled_Is_Not_Entered()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.SendEmcyAsync(0xFF01, 0, default, cancelled.Token));
        History(node).Should().BeEmpty("a cancelled EMCY never reached the bus");

        node.ObjectDictionary.WriteUnsigned(0x1014, 0, 0x8000_0000u | EmcyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.SendEmcyAsync(0xFF02, 0));
        History(node).Should().BeEmpty("bit 31 of 1014h switches the EMCY off");
    }

    // The object holds "the errors that occurred on the CANopen device", not those of its neighbours.
    [Fact]
    public async Task A_Received_Emcy_Is_Not_Entered()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.EmcyReceived += (_, _) => received.TrySetResult(true);

        wire.Transmit(CanOpenCobId.Emcy(0x22), new byte[] { 0x01, 0xFF, 0, 0, 0, 0, 0, 0 });
        await received.Task.WithTimeoutAsync(ShortTimeout);
        History(node).Should().BeEmpty();
    }

    // §7.5.2.4 NOTE, for sub-index 01h with no error present: 0800 0024h or 0800 0000h. The same
    // holds here for every entry above the count, on the two upload paths a server has.
    [Theory]
    [InlineData(HistoryUploadInit)]
    [InlineData(HistoryBlockUploadInit)]
    public async Task An_Entry_Above_The_Count_Cannot_Be_Uploaded(byte command)
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        (await SdoExchangeAsync(wire, command, 0x01, 127)).Should().Equal(HistoryAbort(0x01, SdoAbortCode.NoDataAvailable));

        await node.SendEmcyAsync(0xFF01, 0);
        (await SdoExchangeAsync(wire, command, 0x02, 127)).Should().Equal(HistoryAbort(0x02, SdoAbortCode.NoDataAvailable));
        (await SdoExchangeAsync(wire, command, 0x01, 127))[0].Should().NotBe((byte)0x80, "sub-index 01h holds an error now");
    }

    // Count and entry are one read (Codex on #510). The test holds the write gate, as an application
    // emptying the history on its own thread does. An upload that reads without the gate answers with
    // the error while the gate is held. How long the wait is only decides how surely that is seen:
    // the node cannot answer while the gate is held, so a slow host weakens this test, not breaks it.
    [Theory]
    [InlineData(HistoryUploadInit)]
    [InlineData(HistoryBlockUploadInit)]
    public async Task An_Upload_Does_Not_Read_Between_The_Writes_Of_An_Emptying(byte command)
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        var od = node.ObjectDictionary;

        od.Transaction(() =>
        {
            wire.Transmit(SdoRequest, new byte[] { command, 0x03, 0x10, 0x01, 127, 0, 0, 0 });
            Thread.Sleep(200);
            wire.Payloads(SdoResponse).Should().BeEmpty("the upload waits for the write gate");
            od.WriteUnsigned(0x1003, 0, 0);
        });

        await wire.WaitForCountAsync(SdoResponse, 1);
        wire.Payloads(SdoResponse)[0].Should().Equal(HistoryAbort(0x01, SdoAbortCode.NoDataAvailable));
    }

    [Fact]
    public async Task An_Entry_Within_The_Count_And_The_Count_Are_Uploaded()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        (await SdoExchangeAsync(wire, HistoryUploadInit, 0x00)).Should().Equal(0x4F, 0x03, 0x10, 0x00, 0, 0, 0, 0);

        await node.SendEmcyAsync(0xFF01, 0, new byte[] { 0x34, 0x12 });
        (await SdoExchangeAsync(wire, HistoryUploadInit, 0x00)).Should().Equal(0x4F, 0x03, 0x10, 0x00, 1, 0, 0, 0);
        (await SdoExchangeAsync(wire, HistoryUploadInit, 0x01)).Should().Equal(0x43, 0x03, 0x10, 0x01, 0x01, 0xFF, 0x34, 0x12);
    }

    // §7.5.2.4: writing 00h empties the array, another value is refused with 0609 0030h, and the
    // entries are ro.
    [Fact]
    public async Task Writing_0_Empties_The_History_And_Nothing_Else_Is_Writable()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        await node.SendEmcyAsync(0xFF02, 0);

        (await SdoExchangeAsync(wire, HistoryDownloadOneByte, 0x00, 1)).Should().Equal(HistoryAbort(0x00, SdoAbortCode.ValueRangeExceeded));
        (await SdoExchangeAsync(wire, HistoryDownloadFourBytes, 0x01, 1, 2, 3, 4)).Should().Equal(HistoryAbort(0x01, SdoAbortCode.AttemptWriteReadOnly));
        History(node).Should().Equal(0xFF02u, 0xFF01u);

        (await SdoExchangeAsync(wire, HistoryDownloadOneByte, 0x00, 0))[0].Should().Be((byte)0x60);
        History(node).Should().BeEmpty();
        node.ObjectDictionary.ReadUnsigned(0x1003, 1).Should().Be(0u, "emptied, not just uncounted");
        node.ObjectDictionary.ReadUnsigned(0x1003, 2).Should().Be(0u);
        (await SdoExchangeAsync(wire, HistoryUploadInit, 0x01)).Should().Equal(HistoryAbort(0x01, SdoAbortCode.NoDataAvailable));

        await node.SendEmcyAsync(0xFF03, 0);
        History(node).Should().Equal(0xFF03u); // the history goes on after being emptied
    }

    [Fact]
    public async Task A_Local_Write_Follows_The_Same_Rules()
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 2, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        var od = node.ObjectDictionary;

        Assert.Throws<ArgumentException>(() => od.WriteUnsigned(0x1003, 0, 1));
        Assert.Throws<ArgumentException>(() => od.WriteUnsigned(0x1003, 1, 0x1234));
        History(node).Should().Equal(0xFF01u);
        od.WriteUnsigned(0x1003, 0, 0);
        History(node).Should().BeEmpty();
    }

    // The maintainer's decision on #434: a master that resets a node to recover it can still read
    // why it had to. Status like 1001h: an NMT reset leaves it, 1010h does not store it and 1011h
    // does not restore it, so only power-off and a write of 0 end it.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task An_Nmt_Reset_Keeps_The_History(NmtCommand reset)
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        await node.SendEmcyAsync(0xFF02, 0);

        wire.SendNmt(reset, Device);
        await WaitForStateAsync(node, NmtState.PreOperational);
        History(node).Should().Equal(0xFF02u, 0xFF01u);

        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF03, 0);
        History(node).Should().Equal(0xFF03u, 0xFF02u, 0xFF01u);
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Storing_Does_Not_Take_The_History_Along(NmtCommand reset)
    {
        var session = NewSession();
        using var node = OpenWithHistory(session, 4, out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        node.StoreParameters();
        node.ObjectDictionary.WriteUnsigned(0x1003, 0, 0);

        wire.SendNmt(reset, Device);
        await WaitForStateAsync(node, NmtState.PreOperational);
        History(node).Should().BeEmpty("the store holds parameters, and this is no parameter");
    }

    // FR-CO-027: the file governs the bus. It raises the capacity to what it declares.
    [Fact]
    public async Task A_Description_That_Declares_1003h_Gets_A_Maintained_History()
    {
        var session = NewSession();
        using var node = OpenDescribed(session, EdsWithHistory("rw", "0", "ro", "ro"), out var wire);
        using var _w = wire;
        node.DeviceDescription!.Findings.Where(f => f.Index == 0x1003).Should().BeEmpty();
        node.ObjectDictionary.TryGet(0x1003, 2, out _).Should().BeTrue();
        node.ObjectDictionary.TryGet(0x1003, 3, out _).Should().BeFalse();

        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);
        await node.SendEmcyAsync(0xFF02, 0);
        await node.SendEmcyAsync(0xFF03, 0);
        History(node).Should().Equal(0xFF03u, 0xFF02u);
    }

    [Fact]
    public void The_Option_Raises_What_A_Description_Declares()
    {
        var session = NewSession();
        using var node = OpenDescribed(session, EdsWithHistory("rw", "0", "ro"), out var wire, capacity: 3);
        using var _w = wire;
        node.ObjectDictionary.TryGet(0x1003, 3, out _).Should().BeTrue();
        node.ObjectDictionary.TryGet(0x1003, 4, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("ro")]
    [InlineData("const")]
    public async Task A_Count_The_File_Declares_Read_Only_Cannot_Be_Cleared_Over_The_Bus(string access)
    {
        var session = NewSession();
        using var node = OpenDescribed(session, EdsWithHistory(access, "0", "ro"), out var wire);
        using var _w = wire;
        await StartAsync(wire, node);
        await node.SendEmcyAsync(0xFF01, 0);

        (await SdoExchangeAsync(wire, HistoryDownloadOneByte, 0x00, 0)).Should().Equal(HistoryAbort(0x00, SdoAbortCode.AttemptWriteReadOnly));
        History(node).Should().Equal(0xFF01u);
    }

    // §7.5.2.4 gives the entries as ro and the count a default of 00h, and "at least" one entry.
    [Fact]
    public void What_A_File_Cannot_Promise_Is_Corrected_And_Reported()
    {
        var session = NewSession();
        using var node = OpenDescribed(session, EdsWithHistory("rw", "3", "rw"), out var wire);
        using var _w = wire;
        node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(0u, "the history starts empty, whatever the file says");
        node.ObjectDictionary.TryGet(0x1003, 1, out var entry).Should().BeTrue();
        entry.Access.Should().Be(OdAccess.ReadOnly);
        var findings = node.DeviceDescription!.Findings.Where(f => f.Index == 0x1003).ToList();
        findings.Should().Contain(f => f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        findings.Should().Contain(f => f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.Corrected);
    }

    [Fact]
    public void An_Entry_Above_Sub_Index_FEh_Is_Left_Out_And_Reported()
    {
        var text = EdsTextWithHistory("rw", "0", "ro").Replace("[OptionalObjects]\n",
            "[1003subFF]\nParameterName=Beyond the object\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0\nPDOMapping=0\n\n[OptionalObjects]\n");
        var session = NewSession();
        using var node = OpenDescribed(session, CanOpenDeviceDescription.ParseEds(text), out var wire);
        using var _w = wire;
        node.ObjectDictionary.TryGet(0x1003, 1, out _).Should().BeTrue();
        node.ObjectDictionary.TryGet(0x1003, 2, out _).Should().BeFalse("FFh does not raise the capacity");
        node.ObjectDictionary.TryGet(0x1003, 0xFF, out _).Should().BeFalse();
        node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1003 && f.Subindex == 0xFF && f.Outcome == DeviceDescriptionOutcome.Omitted);
    }

    [Fact]
    public void A_File_Without_An_Entry_Still_Gets_The_One_The_Object_Requires()
    {
        var session = NewSession();
        using var node = OpenDescribed(session, EdsWithHistory("rw", "0"), out var wire);
        using var _w = wire;
        node.ObjectDictionary.TryGet(0x1003, 1, out _).Should().BeTrue();
        node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1003 && f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.SuppliedDefault);
    }
}
