using System;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Tests.TestCases.Properties;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// Seeded property tests for <see cref="IsoTpFrameCodec"/> (issue #210): <c>TryParsePci(Build*(x))</c>
/// gives back <c>x</c> for every valid <c>x</c>, and no byte string makes the parser throw.
/// </summary>
public class IsoTpFrameCodecPropertyTests
{
    private static IsoTpEndpoint RandomEndpoint(Random rng) => rng.Next(3) switch
    {
        0 => IsoTpEndpoint.Normal(0x123, 0x124),
        1 => IsoTpEndpoint.Extended(0x123, 0x124, (byte)rng.Next(256), (byte)rng.Next(256)),
        _ => IsoTpEndpoint.Mixed(0x123, 0x124, (byte)rng.Next(256)),
    };

    private static bool IsValidFdLength(int n) => Array.IndexOf(new[] { 8, 12, 16, 20, 24, 32, 48, 64 }, n) >= 0;

    [Fact]
    public void SingleFrame_RoundTrips_Through_TryParsePci()
    {
        var run = new SeededRun(210_001);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var ep = RandomEndpoint(run.Rng);
            bool fd = run.Rng.Next(2) == 0;
            bool padding = run.Rng.Next(2) == 0;
            byte padByte = (byte)run.Rng.Next(256);
            int max = IsoTpFrameCodec.SingleFrameMaxDataLength(fd, ep.UsesAddressExtension);
            var data = run.Bytes(run.Rng.Next(1, max + 1));
            var because = run.Tag(i, $"fd={fd} padding={padding} mode={ep.AddressingMode} len={data.Length}");

            var frame = IsoTpFrameCodec.BuildSingleFrame(ep, data, fd, padding, padByte);

            frame.Length.Should().BeLessOrEqualTo(fd ? 64 : 8, because);
            if (padding) (fd ? IsValidFdLength(frame.Length) : frame.Length == 8).Should().BeTrue(because);
            if (ep.UsesAddressExtension) frame[0].Should().Be(ep.AddressExtension, because);

            IsoTpFrameCodec.TryParsePci(frame, ep, fd, out var pci).Should().BeTrue(because);
            pci.Type.Should().Be(PciType.SingleFrame, because);
            pci.Length.Should().Be(data.Length, because);
            frame.AsSpan(pci.DataOffset, pci.Length).ToArray().Should().Equal(data, because);
            for (int k = pci.DataOffset + pci.Length; k < frame.Length; k++)
                frame[k].Should().Be(padByte, because + " padding");
        }
    }

    [Fact]
    public void FirstFrame_RoundTrips_Through_TryParsePci()
    {
        var run = new SeededRun(210_002);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var ep = RandomEndpoint(run.Rng);
            bool fd = run.Rng.Next(2) == 0;
            int total = fd
                ? run.Rng.Next(3) switch
                {
                    0 => run.Rng.Next(1, 4096),
                    1 => run.Rng.Next(4090, 4110),
                    _ => run.Rng.Next(4096, int.MaxValue),
                }
                : run.Rng.Next(1, 4096);
            var chunk = run.Bytes(run.Rng.Next(0, 64));
            var because = run.Tag(i, $"fd={fd} mode={ep.AddressingMode} total={total} chunk={chunk.Length}");

            var frame = IsoTpFrameCodec.BuildFirstFrame(ep, total, chunk, fd);

            frame.Length.Should().Be(fd ? 64 : 8, because);
            IsoTpFrameCodec.TryParsePci(frame, ep, fd, out var pci).Should().BeTrue(because);
            pci.Type.Should().Be(PciType.FirstFrame, because);
            pci.Length.Should().Be(total, because);

            bool longForm = total > IsoTpFrameCodec.MaxClassicFirstFrameLength;
            int capacity = IsoTpFrameCodec.FirstFrameMaxDataLength(fd, ep.UsesAddressExtension, longForm);
            pci.DataOffset.Should().Be(frame.Length - capacity, because);
            int carried = Math.Min(chunk.Length, Math.Min(capacity, total));
            frame.AsSpan(pci.DataOffset, carried).ToArray().Should().Equal(chunk.AsSpan(0, carried).ToArray(), because);
        }
    }

    [Fact]
    public void ConsecutiveFrame_RoundTrips_Through_TryParsePci()
    {
        var run = new SeededRun(210_003);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var ep = RandomEndpoint(run.Rng);
            bool fd = run.Rng.Next(2) == 0;
            bool padding = run.Rng.Next(2) == 0;
            byte sn = (byte)run.Rng.Next(16);
            int max = IsoTpFrameCodec.ConsecutiveFrameMaxDataLength(fd, ep.UsesAddressExtension);
            var chunk = run.Bytes(run.Rng.Next(0, max + 1));
            var because = run.Tag(i, $"fd={fd} padding={padding} mode={ep.AddressingMode} sn={sn} len={chunk.Length}");

            var frame = IsoTpFrameCodec.BuildConsecutiveFrame(ep, sn, chunk, fd, padding);

            IsoTpFrameCodec.TryParsePci(frame, ep, fd, out var pci).Should().BeTrue(because);
            pci.Type.Should().Be(PciType.ConsecutiveFrame, because);
            pci.SequenceNumber.Should().Be(sn, because);
            frame.AsSpan(pci.DataOffset, chunk.Length).ToArray().Should().Equal(chunk, because);
        }
    }

    [Fact]
    public void FlowControl_RoundTrips_Through_TryParsePci()
    {
        var run = new SeededRun(210_004);
        var statuses = new[] { FlowStatus.ClearToSend, FlowStatus.Wait, FlowStatus.Overflow };
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var ep = RandomEndpoint(run.Rng);
            bool fd = run.Rng.Next(2) == 0;
            bool padding = run.Rng.Next(2) == 0;
            var status = statuses[run.Rng.Next(statuses.Length)];
            byte bs = (byte)run.Rng.Next(256);
            byte st = (byte)run.Rng.Next(256);
            var because = run.Tag(i, $"fd={fd} padding={padding} mode={ep.AddressingMode} fs={status} bs={bs} st=0x{st:X2}");

            var frame = IsoTpFrameCodec.BuildFlowControl(ep, status, bs, st, fd, padding);

            IsoTpFrameCodec.TryParsePci(frame, ep, fd, out var pci).Should().BeTrue(because);
            pci.Type.Should().Be(PciType.FlowControl, because);
            pci.FlowStatus.Should().Be(status, because);
            pci.BlockSize.Should().Be(bs, because);
            pci.StMinRaw.Should().Be(st, because);
            pci.StMin.Should().Be(IsoTpFrameCodec.DecodeStMin(st), because);
            pci.DataOffset.Should().Be(ep.AddressExtensionSize + 3, because);
        }
    }

    [Fact]
    public void StMin_Encode_Inverts_Decode_On_Every_Defined_Raw_Value()
    {
        // 0x00..0x7F (ms) and 0xF1..0xF9 (100 us steps) are the defined values; the rest decode to 127 ms.
        for (int raw = 0; raw < 256; raw++)
        {
            var decoded = IsoTpFrameCodec.DecodeStMin((byte)raw);
            bool defined = raw <= 0x7F || raw is >= 0xF1 and <= 0xF9;
            if (defined)
                IsoTpFrameCodec.EncodeStMin(decoded).Should().Be((byte)raw, $"raw=0x{raw:X2}");
            else
                decoded.Should().Be(TimeSpan.FromMilliseconds(127), $"reserved raw=0x{raw:X2}");
        }
    }

    [Fact]
    public void StMin_Encode_Is_Monotonic_And_Lands_On_A_Defined_Value()
    {
        var run = new SeededRun(210_005);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var a = TimeSpan.FromTicks(run.Rng.Next(0, 2_000_000)); // 0..200 ms
            var b = TimeSpan.FromTicks(run.Rng.Next(0, 2_000_000));
            if (a > b) (a, b) = (b, a);
            var because = run.Tag(i, $"a={a.Ticks} b={b.Ticks}");

            byte ea = IsoTpFrameCodec.EncodeStMin(a);
            byte eb = IsoTpFrameCodec.EncodeStMin(b);

            (ea <= 0x7F || ea is >= 0xF1 and <= 0xF9).Should().BeTrue(because);
            // Sub-millisecond codes (0xF1..0xF9) sort above 0x7F, so compare the decoded durations.
            IsoTpFrameCodec.DecodeStMin(ea).Should().BeLessOrEqualTo(IsoTpFrameCodec.DecodeStMin(eb), because);
        }
    }

    [Fact]
    public void TryParsePci_Never_Throws_And_Reports_A_Consistent_View_For_Arbitrary_Bytes()
    {
        var run = new SeededRun(210_006);
        for (int i = 0; i < SeededRun.Iterations * 5; i++)
        {
            var ep = RandomEndpoint(run.Rng);
            bool fd = run.Rng.Next(2) == 0;
            var payload = run.Bytes(run.Rng.Next(0, 65));
            // Bias the PCI byte toward the interesting nibbles and the escape headers.
            int pciIndex = ep.AddressExtensionSize;
            if (payload.Length > pciIndex && run.Rng.Next(2) == 0)
                payload[pciIndex] = (byte)((run.Rng.Next(0, 5) << 4) | (run.Rng.Next(4) == 0 ? 0 : run.Rng.Next(16)));
            if (payload.Length > pciIndex + 1 && run.Rng.Next(4) == 0)
                payload[pciIndex + 1] = 0;
            var because = run.Tag(i, $"fd={fd} mode={ep.AddressingMode} bytes={SeededRun.Hex(payload)}");

            bool ok = false;
            Pci pci = default;
            var act = () => ok = IsoTpFrameCodec.TryParsePci(payload, ep, fd, out pci);
            act.Should().NotThrow(because);

            if (!ok)
            {
                pci.Should().Be(default(Pci), because);
                continue;
            }

            pci.DataOffset.Should().BeInRange(pciIndex + 1, payload.Length, because);
            switch (pci.Type)
            {
                case PciType.SingleFrame:
                    pci.Length.Should().BeGreaterThan(0, because);
                    (pci.DataOffset + pci.Length).Should().BeLessOrEqualTo(payload.Length, because);
                    break;
                case PciType.FirstFrame:
                    pci.Length.Should().BeGreaterThan(0, because);
                    break;
                case PciType.ConsecutiveFrame:
                    pci.SequenceNumber.Should().BeLessThan(16, because);
                    break;
                case PciType.FlowControl:
                    ((int)pci.FlowStatus).Should().BeInRange(0, 2, because);
                    break;
                default:
                    Assert.Fail("unknown PCI type accepted; " + because);
                    break;
            }
        }
    }
}
