using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// A node holds up to eight logical devices. Each owns an 800h-wide window of the object
/// dictionary starting at 6000h, and its PDO numbers are 64 apart.
/// </summary>
public class CanOpenLogicalDevicesTests : IClassFixture<VirtualAdapterFixture>
{
    [Theory]
    [InlineData(0, 0x6000, 0x67FF, 0)]
    [InlineData(1, 0x6800, 0x6FFF, 64)]
    [InlineData(7, 0x9800, 0x9FFF, 448)]
    public void Each_Logical_Device_Has_Its_Object_Area_Device_Type_Object_And_Pdo_Offset(
        int index, int areaStart, int deviceTypeIndex, int pdoOffset)
    {
        CanOpenLogicalDevices.ObjectAreaStart(index).Should().Be((ushort)areaStart);
        CanOpenLogicalDevices.DeviceTypeIndex(index).Should().Be((ushort)deviceTypeIndex);
        CanOpenLogicalDevices.PdoNumberOffset(index).Should().Be(pdoOffset);
    }

    [Fact]
    public void A_Profile_Index_Moves_By_800h_Per_Logical_Device()
    {
        CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6200).Should().Be(0x6200);
        CanOpenLogicalDevices.ToAbsoluteIndex(2, 0x6200).Should().Be(0x7200);
        FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x5FFF)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6800)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_Edges_Of_A_Profile_Area_Are_Accepted()
    {
        CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6000).Should().Be(0x6000);
        CanOpenLogicalDevices.ToAbsoluteIndex(7, 0x67FF).Should().Be(0x9FFF);
    }

    [Fact]
    public void A_Logical_Device_Carries_Its_Device_Type_And_Its_Place_In_The_Dictionary()
    {
        var deviceType = new CanOpenDeviceType(0xFFFF0198u);

        var device = new CanOpenLogicalDevice(3, deviceType);

        device.Index.Should().Be(3);
        device.DeviceType.Should().Be(deviceType);
        device.ObjectAreaStart.Should().Be(0x7800);
        device.PdoNumberOffset.Should().Be(192);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void There_Are_Eight_Logical_Devices_And_No_More(int index)
    {
        FluentActions.Invoking(() => CanOpenLogicalDevices.ObjectAreaStart(index)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName("logicalDevice");
        FluentActions.Invoking(() => CanOpenLogicalDevices.DeviceTypeIndex(index)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName("logicalDevice");
        FluentActions.Invoking(() => CanOpenLogicalDevices.PdoNumberOffset(index)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName("logicalDevice");
        FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(index, 0x6000)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName("logicalDevice");
        FluentActions.Invoking(() => new CanOpenLogicalDevice(index, default)).Should().Throw<ArgumentOutOfRangeException>().WithParameterName("index");
    }

    [Fact] // FR-CO-063
    public async Task A_Device_Without_FFFFh_Is_One_Logical_Device_And_Nothing_Else_Is_Asked()
    {
        var script = Script((0x1000, 0x000F0191u));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(0x000F0191u);
        script.Asked.Should().Equal(0x1000);
    }

    [Fact] // FR-CO-063
    public async Task The_Chain_Is_Walked_Until_An_Object_Does_Not_Exist()
    {
        var script = Script((0x1000, 0xFFFF0191u), (0x67FF, 0x000F0191u), (0x6FFF, 0x00010198u));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.DeviceType.Value.Should().Be(0xFFFF0191u);
        result.Devices.Select(d => (d.Index, d.DeviceType.Value))
            .Should().Equal((0, 0x000F0191u), (1, 0x00010198u));
        script.Asked.Should().Equal(0x1000, 0x67FF, 0x6FFF, 0x77FF);
    }

    [Fact] // FR-CO-063
    public async Task Eight_Devices_End_The_Chain_Without_A_Ninth_Request()
    {
        // 1000h names the profile of device 0, 0190h (#552).
        var answers = new List<(ushort, object)> { (0x1000, 0xFFFF0190u) };
        for (var i = 0; i < CanOpenLogicalDevices.MaxCount; i++)
        {
            answers.Add((CanOpenLogicalDevices.DeviceTypeIndex(i), 0x00010190u + (uint)i));
        }

        var script = Script(answers.ToArray());

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.Devices.Select(d => d.Index).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        result.Devices.Select(d => d.DeviceType.Value)
            .Should().Equal(Enumerable.Range(0, 8).Select(i => 0x00010190u + (uint)i));
        script.Asked.Should().HaveCount(9);
        script.Asked.Last().Should().Be(0x9FFF);
    }

    /// <summary>What a read of the chain is expected to report as its failure.</summary>
    public enum ChainFailure
    {
        /// <summary><see cref="CanOpenLogicalDeviceReadResult.Failure"/> is null.</summary>
        None,

        /// <summary>The <see cref="SdoAbortException"/> the upload threw, as it is.</summary>
        Abort,

        /// <summary>An <see cref="InvalidDataException"/> for a value that is not four bytes.</summary>
        WrongLength,

        /// <summary>An <see cref="InvalidDataException"/> for answers that contradict each other,
        /// with the peer's own "object does not exist" as its inner exception.</summary>
        EndedTooEarly,

        /// <summary>An <see cref="InvalidDataException"/> for a <c>67FFh</c> whose profile is not
        /// the one of <c>1000h</c>, without an inner exception.</summary>
        OtherProfile,
    }

    /// <summary>One row of the table in the remarks of <see cref="CanOpenLogicalDevices.ReadAsync"/>.</summary>
    public sealed record ChainCase(
        string Name,
        (ushort Index, object Answer)[] Answers,
        CanOpenLogicalDeviceReadEnd End,
        uint[] Devices,
        ushort[] Asked,
        ChainFailure Failure)
    {
        public override string ToString() => Name;
    }

    private const uint Chain401 = 0xFFFF0191u;
    private const uint Device401 = 0x000F0191u;
    private const uint Device408 = 0x00010198u;

    private static PeerSdoAccessException Refused(ushort index, bool bound)
        => new(1, index, 0, peerDescriptionLoaded: bound, "refused by the gate");

    private static SdoAbortException LocalAbort(ushort index, SdoAbortCode code)
        => new(index, 0, (uint)code, SdoAbortOrigin.Local, "raised locally");

    /// <summary>
    /// The cases of #552 and their neighbours, as answers a peer gives. An index that is not
    /// listed is the peer's own "object does not exist" (<see cref="Script"/>).
    /// </summary>
    public static TheoryData<ChainCase> Chains => new()
    {
        new ChainCase("without FFFFh a chain object the peer holds is not asked",
            [(0x1000, Device401), (0x67FF, Device408)],
            CanOpenLogicalDeviceReadEnd.Complete, [Device401], [0x1000], ChainFailure.None),
        new ChainCase("two devices of 1000h's profile, then the peer's 0602 0000h, is complete",
            // 67FFh's additional information is the device's own (§7.5.2.1); only the profile is 1000h's.
            [(0x1000, Chain401), (0x67FF, 0x00050191u), (0x6FFF, Device408)],
            CanOpenLogicalDeviceReadEnd.Complete, [0x00050191u, Device408], [0x1000, 0x67FF, 0x6FFF, 0x77FF], ChainFailure.None),
        new ChainCase("FFFFh without any profile, chain of profile 0000h, is complete",
            [(0x1000, 0xFFFF0000u), (0x67FF, 0x00000000u), (0x6FFF, Device401)],
            CanOpenLogicalDeviceReadEnd.Complete, [0x00000000u, Device401], [0x1000, 0x67FF, 0x6FFF, 0x77FF], ChainFailure.None),
        new ChainCase("#552 (1): FFFFh and the peer's 0602 0000h at 67FFh",
            [(0x1000, Chain401)],
            CanOpenLogicalDeviceReadEnd.Failed, [Chain401], [0x1000, 0x67FF], ChainFailure.EndedTooEarly),
        new ChainCase("#552 (1): FFFFh, 67FFh, and the peer's 0602 0000h at 6FFFh",
            [(0x1000, Chain401), (0x67FF, Device401)],
            CanOpenLogicalDeviceReadEnd.Failed, [Device401], [0x1000, 0x67FF, 0x6FFF], ChainFailure.EndedTooEarly),
        new ChainCase("#552 (2): 67FFh names another profile than 1000h, and nothing further is asked",
            [(0x1000, Chain401), (0x67FF, Device408), (0x6FFF, Device401)],
            CanOpenLogicalDeviceReadEnd.Failed, [Device408], [0x1000, 0x67FF], ChainFailure.OtherProfile),
        new ChainCase("#552 (2): profile 0000h in 1000h is compared like any other, a 67FFh of profile 401 contradicts it",
            [(0x1000, 0xFFFF0000u), (0x67FF, 0x00010191u), (0x6FFF, Device401)],
            CanOpenLogicalDeviceReadEnd.Failed, [0x00010191u], [0x1000, 0x67FF], ChainFailure.OtherProfile),
        new ChainCase("no description bound: the gate refuses 67FFh",
            [(0x1000, Chain401), (0x67FF, Refused(0x67FF, bound: false))],
            CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired, [Chain401], [0x1000, 0x67FF], ChainFailure.None),
        new ChainCase("the bound description does not declare 67FFh",
            [(0x1000, Chain401), (0x67FF, Refused(0x67FF, bound: true))],
            CanOpenLogicalDeviceReadEnd.EndedByPeerDescription, [Chain401], [0x1000, 0x67FF], ChainFailure.None),
        new ChainCase("the bound description does not declare 6FFFh: one device, but the peer said nothing",
            [(0x1000, Chain401), (0x67FF, Device401), (0x6FFF, Refused(0x6FFF, bound: true))],
            CanOpenLogicalDeviceReadEnd.EndedByPeerDescription, [Device401], [0x1000, 0x67FF, 0x6FFF], ChainFailure.None),
        new ChainCase("a local abort carrying 0602 0000h at 67FFh is not the peer ending the chain",
            [(0x1000, Chain401), (0x67FF, LocalAbort(0x67FF, SdoAbortCode.ObjectDoesNotExist))],
            CanOpenLogicalDeviceReadEnd.Failed, [Chain401], [0x1000, 0x67FF], ChainFailure.Abort),
        new ChainCase("a local abort carrying 0602 0000h at 6FFFh is not the peer ending the chain",
            [(0x1000, Chain401), (0x67FF, Device401), (0x6FFF, LocalAbort(0x6FFF, SdoAbortCode.ObjectDoesNotExist))],
            CanOpenLogicalDeviceReadEnd.Failed, [Device401], [0x1000, 0x67FF, 0x6FFF], ChainFailure.Abort),
        new ChainCase("another peer abort at 6FFFh",
            [(0x1000, Chain401), (0x67FF, Device401),
             (0x6FFF, new SdoAbortException(0x6FFF, 0, (uint)SdoAbortCode.SubIndexDoesNotExist, SdoAbortOrigin.Peer, "peer"))],
            CanOpenLogicalDeviceReadEnd.Failed, [Device401], [0x1000, 0x67FF, 0x6FFF], ChainFailure.Abort),
        new ChainCase("6FFFh is not four bytes",
            [(0x1000, Chain401), (0x67FF, Device401), (0x6FFF, new byte[2])],
            CanOpenLogicalDeviceReadEnd.Failed, [Device401], [0x1000, 0x67FF, 0x6FFF], ChainFailure.WrongLength),
    };

    [Theory] // FR-CO-063, #552
    [MemberData(nameof(Chains))]
    public async Task A_Chain_Ends_As_The_Table_Of_ReadAsync_Says(ChainCase chain)
    {
        var script = Script(chain.Answers);

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(chain.End);
        result.DeviceType.Value.Should().Be((uint)chain.Answers[0].Answer, "1000h is kept as the peer returned it");
        result.Devices.Select(d => (d.Index, d.DeviceType.Value))
            .Should().Equal(chain.Devices.Select((word, index) => (index, word)));
        script.Asked.Should().Equal(chain.Asked);
        result.Inconsistent.Should().Be(chain.Failure is ChainFailure.EndedTooEarly or ChainFailure.OtherProfile);
        switch (chain.Failure)
        {
            case ChainFailure.None:
                result.Failure.Should().BeNull();
                break;
            case ChainFailure.Abort:
                result.Failure.Should().BeSameAs(chain.Answers.Last().Answer);
                break;
            case ChainFailure.WrongLength:
                result.Failure.Should().BeOfType<InvalidDataException>().Which.InnerException.Should().BeNull();
                result.Devices[0].DeviceType.ProfileNumber.Should().Be(result.DeviceType.ProfileNumber);
                break;
            case ChainFailure.EndedTooEarly:
                var early = result.Failure.Should().BeOfType<InvalidDataException>().Subject;
                early.Message.Should().Contain("0xFFFF0191").And.Contain($"{chain.Asked.Last():X4}h");
                var abort = early.InnerException.Should().BeOfType<SdoAbortException>().Subject;
                abort.Origin.Should().Be(SdoAbortOrigin.Peer);
                abort.AbortCode.Should().Be((uint)SdoAbortCode.ObjectDoesNotExist);
                abort.Index.Should().Be(chain.Asked.Last());
                break;
            case ChainFailure.OtherProfile:
                var other = result.Failure.Should().BeOfType<InvalidDataException>().Subject;
                other.InnerException.Should().BeNull();
                var chainWord = (uint)chain.Answers[0].Answer;
                var firstWord = chain.Devices[0];
                other.Message.Should().Contain($"0x{chainWord:X8}").And.Contain($"profile {chainWord & 0xFFFF}")
                    .And.Contain("67FFh").And.Contain($"0x{firstWord:X8}").And.Contain($"profile {firstWord & 0xFFFF}");
                // The public way to tell this from a wrong length (Failure, End): the words disagree.
                result.Devices[0].DeviceType.ProfileNumber.Should().NotBe(result.DeviceType.ProfileNumber);
                break;
        }
    }

    [Fact] // Review Focus 2
    public async Task Without_A_Peer_Description_The_Result_Says_One_Is_Required()
    {
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, new PeerSdoAccessException(1, 0x67FF, 0, peerDescriptionLoaded: false, "x")));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired);
        result.Failure.Should().BeNull();
        var device = result.Devices.Should().ContainSingle().Subject;
        device.Index.Should().Be(0);
        device.DeviceType.Value.Should().Be(0xFFFF0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF);
    }

    [Fact]
    public async Task A_Bound_Description_That_Omits_The_Object_Ends_The_Chain_By_The_Description()
    {
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, new PeerSdoAccessException(1, 0x6FFF, 0, peerDescriptionLoaded: true, "not in the file")));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.EndedByPeerDescription);
        result.Failure.Should().BeNull();
        result.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(0x000F0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF, 0x6FFF);
    }

    [Fact]
    public async Task FFFFh_With_A_Bound_Description_Without_67FFh_Is_Ended_By_The_Description_With_The_Copy_Of_1000h()
    {
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, new PeerSdoAccessException(1, 0x67FF, 0, peerDescriptionLoaded: true, "not in the file")));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.EndedByPeerDescription);
        result.Failure.Should().BeNull();
        var device = result.Devices.Should().ContainSingle().Subject;
        device.Index.Should().Be(0);
        device.DeviceType.Value.Should().Be(0xFFFF0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF);
    }

    [Fact] // Review Focus 3
    public async Task A_Device_Type_Of_The_Wrong_Length_Fails_The_Chain_And_Keeps_What_Was_Read()
    {
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, new byte[2]));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        result.Failure.Should().BeOfType<InvalidDataException>();
        var device = result.Devices.Should().ContainSingle().Subject;
        device.Index.Should().Be(0);
        device.DeviceType.Value.Should().Be(0x000F0191u);
    }

    [Fact]
    public async Task A_Failure_At_67FFh_Itself_Fails_The_Chain_With_The_Copy_Of_1000h()
    {
        var timeout = new SdoAbortException(0x67FF, 0, (uint)SdoAbortCode.SdoProtocolTimedOut,
            SdoAbortOrigin.Local, "timed out");
        var script = Script((0x1000, 0xFFFF0191u), (0x67FF, timeout));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        result.Failure.Should().BeSameAs(timeout);
        var device = result.Devices.Should().ContainSingle().Subject;
        device.Index.Should().Be(0);
        device.DeviceType.Value.Should().Be(0xFFFF0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF);
    }

    [Fact] // Review Focus 4
    public async Task A_Timeout_Or_Another_Abort_Inside_The_Chain_Is_A_Failure_With_Its_Cause()
    {
        var timeout = new SdoAbortException(0x6FFF, 0, (uint)SdoAbortCode.SdoProtocolTimedOut,
            SdoAbortOrigin.Local, "timed out");
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, timeout));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        result.Failure.Should().BeSameAs(timeout);
        result.Devices.Should().ContainSingle().Which.Index.Should().Be(0);

        // A locally raised abort carrying the code a peer uses for a missing object is not the
        // peer saying so: the chain failed, it did not end.
        var local = new SdoAbortException(0x6FFF, 0, (uint)SdoAbortCode.ObjectDoesNotExist,
            SdoAbortOrigin.Local, "refused locally");
        var localScript = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, local));

        var localResult = await CanOpenLogicalDevices.ReadCoreAsync(localScript.Upload, default);

        localResult.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        localResult.Failure.Should().BeSameAs(local);
    }

    [Fact] // FR-CO-063
    public async Task A_Transport_Fault_Inside_The_Chain_Reaches_The_Caller()
    {
        var fault = new CanOpenTransportException("bus off");
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, fault));

        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default))
            .Should().ThrowAsync<CanOpenTransportException>();
        script.Asked.Should().Equal(0x1000, 0x67FF, 0x6FFF);
    }

    [Fact] // Review Focus 5
    public async Task Cancellation_And_A_Failing_1000h_Reach_The_Caller()
    {
        var cancelled = Script((0x1000, 0xFFFF0191u), (0x67FF, new OperationCanceledException()));
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadCoreAsync(cancelled.Upload, default))
            .Should().ThrowAsync<OperationCanceledException>();

        var aborted = Script((0x1000, new SdoAbortException(0x1000, 0, (uint)SdoAbortCode.ObjectDoesNotExist,
            SdoAbortOrigin.Peer, "peer abort")));
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadCoreAsync(aborted.Upload, default))
            .Should().ThrowAsync<SdoAbortException>();

        var wrongLength = Script((0x1000, new byte[2]));
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadCoreAsync(wrongLength.Upload, default))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_Rejects_A_Null_Client_And_A_Node_Id_Outside_1_To_127()
    {
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadAsync(null!, 1))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("client");

        var session = $"canopen-logical-devices-{Guid.NewGuid():N}";
        using var bus = Open(session, 0);
        using var client = CanOpen.OpenNode(bus, 0x7F);
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadAsync(client, 0))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => CanOpenLogicalDevices.ReadAsync(client, 128))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadAsync_Reads_The_Device_Type_Of_A_Peer_Over_SDO()
    {
        var session = $"canopen-logical-devices-{Guid.NewGuid():N}";
        using var peerBus = Open(session, 0);
        using var clientBus = Open(session, 1);
        using var peer = CanOpen.OpenNode(peerBus, 0x21);
        using var client = CanOpen.OpenNode(clientBus, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        peer.ObjectDictionary.WriteUnsigned(0x1000, 0, 0x000F0191);
        await WaitForPreOperationalAsync(peer);

        var result = await CanOpenLogicalDevices.ReadAsync(client, 0x21).WithTimeoutAsync(ShortTimeout);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.DeviceType.Value.Should().Be(0x000F0191u);
        result.Devices.Should().ContainSingle().Which.Index.Should().Be(0);
    }

    [Fact] // FR-CO-063
    public async Task Over_The_Bus_The_Gate_Decides_How_Far_The_Chain_Is_Read()
    {
        var session = $"canopen-logical-devices-{Guid.NewGuid():N}";
        using var peerBus = Open(session, 0);
        using var clientBus = Open(session, 1);
        using var server = CanOpen.OpenNode(peerBus, 0x10);
        using var client = CanOpen.OpenNode(clientBus, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        server.ObjectDictionary.WriteUnsigned(0x1000, 0, 0xFFFF0191u);
        server.ObjectDictionary.AddU32(0x67FF, 0, 0x000F0191u, OdAccess.ReadOnly);
        server.ObjectDictionary.AddU32(0x6FFF, 0, 0x00010198u, OdAccess.ReadOnly);
        await WaitForPreOperationalAsync(server);

        // no description bound
        var unbound = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        unbound.End.Should().Be(CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired);
        unbound.Devices.Should().ContainSingle();

        // description lists 1000h, 67FFh, 6FFFh: it does not declare 77FFh, so the description ends the chain
        client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF));
        var bound = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        bound.End.Should().Be(CanOpenLogicalDeviceReadEnd.EndedByPeerDescription);
        bound.Devices.Select(d => d.DeviceType.ProfileNumber).Should().Equal((ushort)401, (ushort)408);

        // description also declares 77FFh, the server holds none: the peer's own "object does not exist"
        client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF, 0x77FF));
        var declared = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        declared.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        declared.Devices.Select(d => d.DeviceType.ProfileNumber).Should().Equal((ushort)401, (ushort)408);
    }

    /// <summary>
    /// The first reproduction of #552 over the bus, against a node whose dictionary gives those
    /// answers: <c>1000h</c> announces several logical devices, and the node itself reports the
    /// first, then the second, device-type object as non-existent. The node is plain: an
    /// application-set device type could not publish the first form, which the node refuses
    /// since #551, so the test writes the dictionary as <see cref="Over_The_Bus_The_Gate_Decides_How_Far_The_Chain_Is_Read"/> does.
    /// </summary>
    [Fact] // FR-CO-063, #552
    public async Task Over_The_Bus_A_Peer_That_Announces_Several_Devices_And_Holds_Fewer_Than_Two_Is_Inconsistent()
    {
        var session = $"canopen-logical-devices-{Guid.NewGuid():N}";
        using var peerBus = Open(session, 0);
        using var clientBus = Open(session, 1);
        using var client = CanOpen.OpenNode(clientBus, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        // The description lists every object asked, so the peer, not the gate, ends the chain.
        client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF));
        var bootup = new BootupWatch(client, 0x10);
        using var server = CanOpen.OpenNode(peerBus, 0x10);
        server.ObjectDictionary.WriteUnsigned(0x1000, 0, Chain401);
        await bootup.First;

        var none = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        none.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        none.Failure.Should().BeOfType<InvalidDataException>()
            .Which.InnerException.Should().BeOfType<SdoAbortException>().Which.Index.Should().Be(0x67FF);
        none.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(Chain401);

        server.ObjectDictionary.AddU32(0x67FF, 0, Device401, OdAccess.ReadOnly);
        var one = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        one.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        one.Failure.Should().BeOfType<InvalidDataException>()
            .Which.InnerException.Should().BeOfType<SdoAbortException>().Which.Index.Should().Be(0x6FFF);
        one.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(Device401);
    }

    /// <summary>
    /// The second reproduction of #552 over the bus: <c>1000h</c> names profile 401, <c>67FFh</c>
    /// profile 408, a second device follows and the node ends the chain itself. Both words are kept
    /// as read, and a catalogue that knows both profiles selects neither.
    /// </summary>
    [Fact] // FR-CO-063, #552
    public async Task Over_The_Bus_A_67FFh_Of_Another_Profile_Than_1000h_Is_Inconsistent_And_No_Profile_Is_Selected()
    {
        var session = $"canopen-logical-devices-{Guid.NewGuid():N}";
        using var peerBus = Open(session, 0);
        using var clientBus = Open(session, 1);
        using var client = CanOpen.OpenNode(clientBus, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF, 0x77FF));
        var bootup = new BootupWatch(client, 0x10);
        using var server = CanOpen.OpenNode(peerBus, 0x10);
        server.ObjectDictionary.WriteUnsigned(0x1000, 0, Chain401);
        server.ObjectDictionary.AddU32(0x67FF, 0, Device408, OdAccess.ReadOnly);
        server.ObjectDictionary.AddU32(0x6FFF, 0, Device401, OdAccess.ReadOnly);
        await bootup.First;

        var result = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Failed);
        result.Failure.Should().BeOfType<InvalidDataException>();
        result.DeviceType.ProfileNumber.Should().Be(401);
        result.Devices.Should().ContainSingle().Which.DeviceType.ProfileNumber.Should().Be(408);
        var catalog = new CanOpenProfileCatalog(new Cia401Profile(), new Cia408Profile());
        FluentActions.Invoking(() => catalog.CreatePeers(client, 0x10, result))
            .Should().Throw<ArgumentException>().WithParameterName("result");
    }

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Completes when the producer's boot-up arrives: it is then in Pre-operational and
    /// its SDO server answers.</summary>
    private sealed class BootupWatch
    {
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BootupWatch(ICanOpenNode observer, byte producer)
        {
            observer.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId == producer && e.State == NmtState.Initializing) _first.TrySetResult(true);
            };
        }

        public Task First => _first.Task.WithTimeoutAsync(ShortTimeout);
    }

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static async Task WaitForPreOperationalAsync(ICanOpenNode node)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.State != NmtState.PreOperational)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Node 0x{node.NodeId:X2} did not reach PreOperational; it is in {node.State}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The upload delegate of a peer: a <c>uint</c> answer is its four little-endian bytes, a
    /// <c>byte[]</c> is returned as it is, an <see cref="Exception"/> is thrown, and an index that
    /// is not scripted is a peer abort "object does not exist". Every index asked is recorded.
    /// </summary>
    private static ScriptedPeer Script(params (ushort Index, object Answer)[] answers)
        => new(answers.ToDictionary(a => a.Index, a => a.Answer));

    private sealed class ScriptedPeer
    {
        private readonly Dictionary<ushort, object> _answers;

        public ScriptedPeer(Dictionary<ushort, object> answers) => _answers = answers;

        public List<ushort> Asked { get; } = new();

        public Task<byte[]> Upload(ushort index, CancellationToken cancellationToken)
        {
            Asked.Add(index);
            if (!_answers.TryGetValue(index, out var answer))
            {
                throw new SdoAbortException(index, 0, (uint)SdoAbortCode.ObjectDoesNotExist,
                    SdoAbortOrigin.Peer, "no such object");
            }

            return answer switch
            {
                uint value => Task.FromResult(BitConverter.GetBytes(value)),
                byte[] bytes => Task.FromResult(bytes),
                Exception ex => throw ex,
                _ => throw new InvalidOperationException("Unscripted answer type."),
            };
        }
    }
}
