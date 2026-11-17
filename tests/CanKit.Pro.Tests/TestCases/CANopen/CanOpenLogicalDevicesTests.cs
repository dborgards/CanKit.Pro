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
        FluentActions.Invoking(() => CanOpenLogicalDevices.ObjectAreaStart(index)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new CanOpenLogicalDevice(index, default)).Should().Throw<ArgumentOutOfRangeException>();
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
        var answers = new List<(ushort, object)> { (0x1000, 0xFFFF0191u) };
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

    [Fact] // Review Focus 1
    public async Task FFFFh_Without_67FFh_Still_Reports_The_First_Device_From_1000h()
    {
        var script = Script((0x1000, 0xFFFF0191u));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        var device = result.Devices.Should().ContainSingle().Subject;
        device.Index.Should().Be(0);
        device.DeviceType.Value.Should().Be(0xFFFF0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF);
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
    public async Task A_Bound_Description_That_Omits_The_Object_Ends_The_Chain_As_Complete()
    {
        var script = Script(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u),
            (0x6FFF, new PeerSdoAccessException(1, 0x6FFF, 0, peerDescriptionLoaded: true, "not in the file")));

        var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);

        result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        result.Failure.Should().BeNull();
        result.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(0x000F0191u);
        script.Asked.Should().Equal(0x1000, 0x67FF, 0x6FFF);
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

        // description lists 1000h, 67FFh, 6FFFh
        client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF));
        var bound = await CanOpenLogicalDevices.ReadAsync(client, 0x10).WithTimeoutAsync(ShortTimeout);
        bound.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
        bound.Devices.Select(d => d.DeviceType.ProfileNumber).Should().Equal((ushort)401, (ushort)408);
    }

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

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
