using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// SDO protocol-correctness tests (FR-CO-002 classic SDO, FR-CO-003 segmented, FR-CO-004 block
/// transfer) that drive one side of the exchange with raw frames so the assertion is about what
/// went on the wire, not only about the round-trip result. Companion to
/// <see cref="CanOpenNodeIntegrationTests"/> (happy paths) and
/// <see cref="CanOpenBlockAndGuardingTests"/> (block round-trips and retransmission).
/// </summary>
public class CanOpenSdoCorrectnessTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"canopen-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static void Send(ICanBus bus, uint cobId, byte[] payload)
        => bus.Transmit(CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false));

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 — CiA 301 v4.2.0 §7.2.4.3.16 defines the block-transfer CRC by its parameters
    // (x^16 + x^12 + x^5 + 1, 16 bit, initial value 0000h) and supplies one check value: the CRC
    // of "123456789" is 31C3h. Pinning that value is what turns "we implement CRC-16/XMODEM"
    // from a claim into a measurement: a wrong polynomial, a wrong initial value, a reflected
    // variant or a final XOR would each produce a different check value.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Crc16_Matches_The_CiA301_Check_Value()
    {
        var crc = SdoBlockFrames.ComputeCrc16Xmodem(Encoding.ASCII.GetBytes("123456789"));
        crc.Should().Be(0x31C3, "CiA 301 §7.2.4.3.16 gives 31C3h as the CRC of \"123456789\"");
    }

    // Raw-frame tap: queues every frame on a given COB-ID so the test body can drive a fake
    // peer deterministically from its own thread (no in-handler transmits).
    private sealed class FrameTap : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly uint _cobId;
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _frames = new();

        public FrameTap(ICanBus bus, uint cobId)
        {
            _bus = bus;
            _cobId = cobId;
            _bus.FrameObserved += OnFrame;
        }

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            if ((uint)e.CanFrame.ID == _cobId)
            {
                _frames.Add(e.CanFrame.Data.ToArray());
            }
        }

        public byte[] Next(TimeSpan timeout)
        {
            if (!_frames.TryTake(out var frame, timeout))
            {
                throw new TimeoutException($"No frame on COB-ID 0x{_cobId:X3} within {timeout}.");
            }
            return frame;
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }
}
