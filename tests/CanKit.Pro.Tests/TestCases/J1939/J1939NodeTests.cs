using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.J1939;

/// <summary>
/// Virtual-loopback integration tests for the SAE J1939 application-layer node
/// (<c>CanKit.Pro.J1939</c>), covering SRS FR-J1939-001..006 Must and FR-J1939-007 Should.
/// </summary>
public class J1939NodeTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"j1939-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    /// <summary>Constructs a NAME parameterized by a caller-controlled identity number so
    /// tests can force a deterministic winner in a claim conflict (numerically-lower NAME
    /// wins per SAE J1939-81 §4.4.3.2).</summary>
    private static J1939Name Name(uint identity, ushort manufacturerCode = 0x100) =>
        new J1939Name(
            identityNumber: identity,
            manufacturerCode: manufacturerCode,
            ecuInstance: 0,
            functionInstance: 0,
            function: 0x81,
            reserved: false,
            vehicleSystem: 0,
            vehicleSystemInstance: 0,
            industryGroup: 0,
            arbitraryAddressCapable: false);

    private static async Task<J1939Message> WaitForMessageAsync(
        IJ1939Node node,
        Func<J1939Message, bool> predicate,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<J1939Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<J1939Message> handler = (_, msg) =>
        {
            if (predicate(msg)) tcs.TrySetResult(msg);
        };
        node.MessageReceived += handler;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            node.MessageReceived -= handler;
        }
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-001: PGN send/receive with 29-bit Priority/PF/PS/SA encode/decode.
    // ---------------------------------------------------------------------------------------

    // PDU2 (broadcast) PGN round-trip. Payload arrives on the receiver with the correct PGN,
    // priority and source address decoded back out of the 29-bit ID.
    [Fact]
    public async Task Pdu2_SingleFrameRoundtrip_DecodesPgnPriorityAndSa()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await sender.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x22).WithTimeout(ShortTimeout);

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var message = new J1939Message(pgn: 0xFEF1u, payload: payload, priority: 5,
            destinationAddress: 0xFF); // PDU2, DA ignored

        var receiveTask = WaitForMessageAsync(receiver, m => m.Pgn == 0xFEF1u, ShortTimeout);
        await sender.SendAsync(message).WithTimeout(ShortTimeout);

        var received = await receiveTask;
        received.Pgn.Should().Be(0xFEF1u);
        received.Priority.Should().Be(5);
        received.SourceAddress.Should().Be(0x11);
        received.DestinationAddress.Should().Be(0xFF);
        received.Payload.ToArray().Should().Equal(payload);
        received.WasMultiFrame.Should().BeFalse();
    }

    // PDU1 (peer-to-peer) PGN round-trip. Destination address is preserved and the PGN group
    // extension is stripped correctly (PDU1 PS is a destination, not part of the PGN).
    [Fact]
    public async Task Pdu1_SingleFrameRoundtrip_DecodesDestinationAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await sender.ClaimAddressAsync(0x33).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x44).WithTimeout(ShortTimeout);

        // 0xEF00 is a PDU1 PGN (PF=0xEF < 240). The wire ID encodes destination in PS.
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var message = new J1939Message(pgn: 0xEF00u, payload: payload, priority: 6,
            destinationAddress: 0x44);

        var receiveTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xEF00u && m.SourceAddress == 0x33, ShortTimeout);
        await sender.SendAsync(message).WithTimeout(ShortTimeout);

        var received = await receiveTask;
        received.Pgn.Should().Be(0xEF00u);
        received.SourceAddress.Should().Be(0x33);
        received.DestinationAddress.Should().Be(0x44);
        received.Payload.ToArray().Should().Equal(payload);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-002: SPN scale/offset extraction.
    // ---------------------------------------------------------------------------------------

    // Well-known SPN 190 (Engine Speed, PGN 61444 / EEC1): 16-bit little-endian value at
    // byte offset 3, resolution 0.125 rpm/bit, offset 0. 8000 rpm ⇒ raw 64000 ⇒ 0x00 0xFA.
    [Fact]
    public void Spn_ExtractsScaleAndOffset()
    {
        var payload = new byte[8];
        // Byte 3 = 0x00, byte 4 = 0xFA (little-endian raw 64000).
        payload[3] = 0x00; payload[4] = 0xFA;
        var engineSpeed = J1939Spn.Extract(payload, byteOffset: 3, startBit: 0,
            bitLength: 16, resolution: 0.125, offset: 0.0);
        engineSpeed.IsValid.Should().BeTrue();
        engineSpeed.Value.Should().BeApproximately(8000.0, 0.001);
    }

    // Cross-byte-boundary 4-bit SPN with an offset (e.g. a temperature-style transform).
    [Fact]
    public void Spn_ExtractsCrossByteWithOffset()
    {
        var payload = new byte[] { 0b1111_0000, 0b0000_1010 };
        // Field spans byte 0 bits 4..7 and byte 1 bits 0..3 = 8 bits, little-endian.
        // = ( (0b0000_1010 & 0x0F) << 4 ) | ( (0b1111_0000 >> 4) & 0x0F ) = 0xAF = 175.
        var physical = J1939Spn.Extract(payload, byteOffset: 0, startBit: 4,
            bitLength: 8, resolution: 1.0, offset: -40.0);
        physical.Value.Should().Be(175 - 40.0);
    }

    // Round-trip via WriteRaw so encoders and decoders are consistent.
    [Fact]
    public void Spn_WriteRaw_RoundTripsWithExtract()
    {
        var payload = new byte[8];
        J1939Spn.WriteRaw(payload, byteOffset: 2, startBit: 3, bitLength: 12, rawValue: 0xABC);
        J1939Spn.ExtractRaw(payload, byteOffset: 2, startBit: 3, bitLength: 12).Should().Be(0xABC);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-003: Address claiming with NAME arbitration (winner keeps address, loser goes
    // to Cannot-Claim).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AddressClaim_TwoNodes_SameAddress_LowerNameWins()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        // Lower identity number ⇒ lower 64-bit NAME ⇒ higher claim priority (§4.4.3.2).
        var winnerName = Name(identity: 0x0000AA);
        var loserName = Name(identity: 0x0000BB);

        // Shrink the arbitration window so the test finishes quickly (default is 250 ms).
        var optsWinner = new J1939NodeOptions(winnerName) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };
        var optsLoser = new J1939NodeOptions(loserName) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };

        using var winner = J1939Node.Open(busA, optsWinner);
        using var loser = J1939Node.Open(busB, optsLoser);

        // Both try 0x50 essentially concurrently; the actor loops each process the peer's
        // announcement and one of them yields.
        var winnerTask = winner.ClaimAddressAsync(0x50);
        var loserTask = loser.ClaimAddressAsync(0x50);

        // Winner (numerically-lower NAME) must succeed.
        await winnerTask.WithTimeout(ShortTimeout);
        winner.ClaimState.Should().Be(J1939ClaimState.Claimed);
        winner.Address.Should().Be((byte)0x50);

        // Loser must fault with J1939CannotClaimException per FR-J1939-004.
        Func<Task> act = () => loserTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939CannotClaimException>()).Which;
        ex.PreferredAddress.Should().Be((byte)0x50);
        loser.ClaimState.Should().Be(J1939ClaimState.CannotClaim);
        loser.Address.Should().BeNull();
    }

    // Regression for #23: two nodes sharing one service must still arbitrate on a bus whose
    // echoes are flagged.
    //
    // `J1939Node.Open(ICanBusService, ...)` documents that nodes with different NAME identities
    // may share one service. On a flagging adapter every frame either node sends is marked
    // IsEcho, because the flag identifies the host and not the node. Withholding echoes therefore
    // hid each node's Address Claim from the other, and both would finish claiming the same
    // address without `HasHigherClaimPriorityThan` ever being consulted — the wrong result on
    // both nodes, with no error raised anywhere.
    //
    // The two-bus arbitration test above cannot catch that: with two buses there is no host echo.
    [Fact]
    public async Task Two_Nodes_Sharing_One_Service_Still_Arbitrate_On_A_Flagging_Echo_Bus()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        // Lower identity number ⇒ lower 64-bit NAME ⇒ higher claim priority (§4.4.3.2).
        var winnerOpts = new J1939NodeOptions(Name(identity: 0x0000AA))
        { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };
        var loserOpts = new J1939NodeOptions(Name(identity: 0x0000BB))
        { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };

        using var winner = J1939Node.Open(service, winnerOpts);
        using var loser = J1939Node.Open(service, loserOpts);

        var winnerTask = winner.ClaimAddressAsync(0x50);
        var loserTask = loser.ClaimAddressAsync(0x50);

        await winnerTask.WithTimeout(ShortTimeout);
        winner.ClaimState.Should().Be(J1939ClaimState.Claimed);
        winner.Address.Should().Be((byte)0x50);

        Func<Task> act = () => loserTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939CannotClaimException>(
            "the sibling's Address Claim is arbitration input even though the host echo flag "
            + "marks it exactly like this node's own claim")).Which;
        ex.PreferredAddress.Should().Be((byte)0x50);
        loser.ClaimState.Should().Be(J1939ClaimState.CannotClaim);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-004: Cannot-Claim broadcasts SA=0xFE.
    // ---------------------------------------------------------------------------------------

    // A node whose preferred address is contested by a peer with a lower NAME MUST broadcast
    // Cannot Claim Address (PGN 0xEE00, SA=0xFE) per SAE J1939-81 §4.4.3.4. We observe the
    // raw frame on the bus so the assertion is independent of the node's own transitions.
    [Fact]
    public async Task CannotClaim_BroadcastsWithNullSourceAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2); // spectator

        var cannotClaimSeen = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        busC.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (J1939Pgn.IsAddressClaim(fields.Pgn) && fields.SourceAddress == J1939Pgn.NullAddress)
                cannotClaimSeen.TrySetResult((uint)e.CanFrame.ID);
        };

        var winnerOpts = new J1939NodeOptions(Name(0x000010)) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };
        var loserOpts = new J1939NodeOptions(Name(0x000020)) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };

        using var winner = J1939Node.Open(busA, winnerOpts);
        using var loser = J1939Node.Open(busB, loserOpts);

        var winnerTask = winner.ClaimAddressAsync(0x60);
        var loserTask = loser.ClaimAddressAsync(0x60);

        await winnerTask.WithTimeout(ShortTimeout);
        Func<Task> act = () => loserTask.WithTimeout(ShortTimeout);
        await act.Should().ThrowAsync<J1939CannotClaimException>();

        var canId = await cannotClaimSeen.Task.AsTaskWithTimeout(ShortTimeout);
        var decomposed = J1939Id.Decompose(canId);
        decomposed.SourceAddress.Should().Be(J1939Pgn.NullAddress);
        decomposed.PduSpecific.Should().Be(J1939Pgn.GlobalAddress);
        J1939Pgn.IsAddressClaim(decomposed.Pgn).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-004 (arbitrary-address fallback, J1939-81 §4.5): after losing the preferred
    // address to a higher-priority NAME, an arbitrary-capable node retries with the next
    // candidate from the arbitrary field and claims it instead of going Cannot-Claim.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AddressClaim_ArbitraryFallback_ClaimsNextFreeAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        var nodeName = Name(0x0000BB);
        var peerName = Name(0x000001); // numerically lower ⇒ wins every contest it enters
        var opts = new J1939NodeOptions(nodeName)
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
            EnableArbitraryAddressClaiming = true,
        };
        using var node = J1939Node.Open(busA, opts);

        // Fake peer: contest only the preferred address 0x80 (claims it with a lower NAME),
        // stay silent on every other candidate.
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0x80 || !J1939Pgn.IsAddressClaim(fields.Pgn)) return;
            if (e.CanFrame.Data.Length < 8) return;
            var claimerName = J1939Name.Decompose(BitConverter.ToUInt64(e.CanFrame.Data.ToArray(), 0));
            if (claimerName.Value != nodeName.Value) return;
            busB.Transmit(CanFrame.Classic(
                (int)J1939Id.ComposePgn(6, 0xEE00u, 0x80, J1939Pgn.GlobalAddress),
                BitConverter.GetBytes(peerName.Value), isExtendedFrame: true));
        };

        await node.ClaimAddressAsync(0x80).WithTimeout(ShortTimeout);

        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x81,
            "after losing 0x80 the node must retry with the next arbitrary-field candidate (J1939-81 §4.5)");
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-004 (SRS verification "all addresses taken"): a peer that contests EVERY
    // candidate drives the arbitrary-address scan to exhaustion — the node must then signal
    // Cannot-Claim (SA=0xFE) exactly as if no fallback existed.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AddressClaim_ArbitraryFallback_ExhaustsField_ThenCannotClaim()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2); // spectator for the final Cannot-Claim broadcast

        var nodeName = Name(0x0000BB);
        var peerName = Name(0x000001);
        var opts = new J1939NodeOptions(nodeName)
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
            EnableArbitraryAddressClaiming = true,
        };
        using var node = J1939Node.Open(busA, opts);

        var cannotClaimSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        busC.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (J1939Pgn.IsAddressClaim(fields.Pgn) && fields.SourceAddress == J1939Pgn.NullAddress)
                cannotClaimSeen.TrySetResult(null);
        };

        // Fake peer: contest EVERY address in the arbitrary field the node tries.
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (!J1939Pgn.IsAddressClaim(fields.Pgn)) return;
            var sa = fields.SourceAddress;
            if (sa is < 0x80 or > 0xF7) return;
            if (e.CanFrame.Data.Length < 8) return;
            var claimerName = J1939Name.Decompose(BitConverter.ToUInt64(e.CanFrame.Data.ToArray(), 0));
            if (claimerName.Value != nodeName.Value) return;
            busB.Transmit(CanFrame.Classic(
                (int)J1939Id.ComposePgn(6, 0xEE00u, sa, J1939Pgn.GlobalAddress),
                BitConverter.GetBytes(peerName.Value), isExtendedFrame: true));
        };

        Func<Task> act = () => node.ClaimAddressAsync(0x80).WithTimeout(TimeSpan.FromSeconds(15));
        var ex = (await act.Should().ThrowAsync<J1939CannotClaimException>()).Which;
        ex.PreferredAddress.Should().Be((byte)0x80);
        node.ClaimState.Should().Be(J1939ClaimState.CannotClaim);
        node.Address.Should().BeNull();

        await cannotClaimSeen.Task.AsTaskWithTimeout(ShortTimeout);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-005: Request-PGN (PGN 0xEA00) send/receive.
    // ---------------------------------------------------------------------------------------

    // A requester sends Request-PGN(0xFEF1) to the global address; the responder application
    // observes the request on MessageReceived and answers with the requested PGN. The
    // requester's inbox then sees the answer.
    [Fact]
    public async Task RequestPgn_ResponderReceivesAndAppReplies()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var requester = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var responder = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await requester.ClaimAddressAsync(0x71).WithTimeout(ShortTimeout);
        await responder.ClaimAddressAsync(0x72).WithTimeout(ShortTimeout);

        const uint requestedPgn = 0xFEF1u;
        var answerPayload = new byte[] { 0x01, 0x02, 0x03 };

        // Responder listens for Request-PGN and answers with the requested PGN.
        responder.MessageReceived += async (_, msg) =>
        {
            if (msg.Pgn != J1939Pgn.Request) return;
            if (msg.Payload.Length < 3) return;
            uint askedFor = (uint)(msg.Payload.Span[0]
                | (msg.Payload.Span[1] << 8)
                | (msg.Payload.Span[2] << 16));
            if (askedFor != requestedPgn) return;
            try
            {
                await responder.SendAsync(new J1939Message(requestedPgn, answerPayload));
            }
            catch { /* observed via BackgroundExceptionOccurred */ }
        };

        var answerTask = WaitForMessageAsync(requester,
            m => m.Pgn == requestedPgn && m.SourceAddress == 0x72,
            ShortTimeout);

        await requester.RequestPgnAsync(requestedPgn).WithTimeout(ShortTimeout);
        var answer = await answerTask;
        answer.Payload.ToArray().Should().Equal(answerPayload);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-006: > 8-byte payload routes through J1939-TP; <= 8-byte payload is direct.
    // ---------------------------------------------------------------------------------------

    // A ≤ 8-byte payload MUST NOT trigger any TP.CM/TP.DT frame on the bus; instead exactly
    // one direct 29-bit frame carries the PGN.
    [Fact]
    public async Task Send_SmallPayload_UsesSingleFramePath()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var spectator = Open(session, 2);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        await sender.ClaimAddressAsync(0x81).WithTimeout(ShortTimeout);

        int tpFrames = 0;
        int singleFrames = 0;
        spectator.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0x81) return;
            if (J1939Pgn.IsTransportCm(fields.Pgn) || J1939Pgn.IsTransportDt(fields.Pgn))
                Interlocked.Increment(ref tpFrames);
            else if (fields.Pgn == 0xFEF2u)
                Interlocked.Increment(ref singleFrames);
        };

        await sender.SendAsync(new J1939Message(0xFEF2u, new byte[] { 1, 2, 3, 4, 5, 6 }))
            .WithTimeout(ShortTimeout);

        // Wait deterministically for exactly one single-frame observation instead of relying
        // on a fixed 50 ms sleep (Copilot 3600424648): the fixed delay was flaky on slow CI
        // runners, and the previous `> 0` assertion silently accepted duplicates.
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (Volatile.Read(ref singleFrames) < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Volatile.Read(ref tpFrames).Should().Be(0,
            "a ≤ 8-byte payload must not use J1939-TP");
        Volatile.Read(ref singleFrames).Should().Be(1,
            "exactly one direct 29-bit frame must carry the PGN");
    }

    [Fact]
    public async Task Send_InvalidPriority_ThrowsBeforeRouting()
    {
        var session = NewSession();
        using var busA = Open(session, 0);

        using var node = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        await node.ClaimAddressAsync(0x82).WithTimeout(ShortTimeout);

        var payloads = new[]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
        };

        foreach (var payload in payloads)
        {
            Func<Task> send = () => node.SendAsync(new J1939Message(0xFEF2u, payload, priority: 8));
            var ex = (await send.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which;
            ex.ParamName.Should().Be("Priority");
        }
    }

    // A > 8-byte payload broadcast MUST use J1939-TP.BAM. We watch for TP.CM frames from the
    // sender on the bus and require the receiver's application PGN to arrive on
    // MessageReceived (proving the whole TP session ran through and reassembled).
    [Fact]
    public async Task Send_LargePayload_UsesJ1939TpBamPath()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var spectator = Open(session, 2);

        // Shorten Th so the multi-frame test runs in <1s while still exercising the timer.
        var senderOpts = new J1939NodeOptions(Name(1))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        };
        var receiverOpts = new J1939NodeOptions(Name(2))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        };

        using var sender = J1939Node.Open(busA, senderOpts);
        using var receiver = J1939Node.Open(busB, receiverOpts);

        await sender.ClaimAddressAsync(0x91).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x92).WithTimeout(ShortTimeout);

        int tpCmSeen = 0;
        spectator.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0x91) return;
            if (J1939Pgn.IsTransportCm(fields.Pgn)) Interlocked.Increment(ref tpCmSeen);
        };

        var payload = new byte[20];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0x40 + i);

        var recvTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xFECAu && m.Payload.Length == 20, ShortTimeout);

        await sender.SendAsync(new J1939Message(0xFECAu, payload,
            destinationAddress: 0xFF)).WithTimeout(ShortTimeout);

        var received = await recvTask;
        received.Payload.ToArray().Should().Equal(payload);
        received.SourceAddress.Should().Be(0x91);
        received.WasMultiFrame.Should().BeTrue();
        Volatile.Read(ref tpCmSeen).Should().BeGreaterThan(0,
            ">8-byte payload must route through J1939-TP (a TP.CM announce must appear on the bus)");
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600377721 regression: after a successful address claim the node MUST accept
    // directed TP.CM traffic to the claimed SA. Before the fix J1939NodeImpl kept its internal
    // IJ1939TpChannel bound to the 0xFE placeholder SA even after ClaimState==Claimed, so
    // J1939TpChannel's `destination == SA || 0xFF` filter dropped every directed CM to the
    // claimed address.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task DirectedTpCm_ToClaimedAddress_IsReceivedAfterClaim()
    {
        var session = NewSession();
        using var busA = Open(session, 0); // peer: raw J1939-TP sender
        using var busB = Open(session, 1); // node under test

        // The receiver is a J1939 node — the whole point is to verify the *node* reassembles
        // and surfaces the directed multi-frame PDU on MessageReceived.
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        });
        await receiver.ClaimAddressAsync(0xA0).WithTimeout(ShortTimeout);
        receiver.ClaimState.Should().Be(J1939ClaimState.Claimed);
        receiver.Address.Should().Be((byte)0xA0);

        // Peer sends a directed TP.CM (>8 bytes) targeting the claimed SA 0xA0. Uses a raw
        // J1939-TP channel from a different SA so the frames actually travel across the
        // virtual bus and hit the node's transport RX filter.
        using var peerTp = CanKit.Pro.J1939Tp.J1939Tp.Open(busA, sourceAddress: 0x55,
            new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)));

        var payload = new byte[24];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0xB0 + i);

        var recvTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xEF00u && m.Payload.Length == payload.Length && m.SourceAddress == 0x55,
            ShortTimeout);

        await peerTp.SendCmAsync(pgn: 0xEF00u, destinationAddress: 0xA0, payload)
            .WithTimeout(ShortTimeout);

        var received = await recvTask;
        received.Payload.ToArray().Should().Equal(payload);
        received.SourceAddress.Should().Be(0x55);
        received.DestinationAddress.Should().Be(0xA0);
        received.WasMultiFrame.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600440955 regression: cancelling ClaimAddressAsync during the arbitration
    // window MUST tear down the pending claim on the actor and prevent the arbitration timer
    // from later committing the address. Before the fix, the cancellation registration only
    // called TrySetCanceled on the returned task; OnClaimAnnounceElapsed still fired and
    // moved ClaimState to Claimed, silently contradicting the observed cancellation.
    //
    // Two details decide whether this test can still see that regression, and both are easy
    // to get wrong (this one did, twice -- once by polling, once by cancelling too early):
    //
    //   * WHEN to cancel. BeginClaimRound publishes Claiming *before* it registers the pending
    //     claim, and the arbitration deadline is armed later still, by
    //     OnClaimAnnounceTxConfirmed once the announcement is confirmed on the wire. That
    //     method returns early if the claim task is already completed -- so cancelling on the
    //     Claiming transition means no deadline is ever armed, OnClaimAnnounceElapsed never
    //     runs, and there is nothing left to commit the address. The test would pass against
    //     the very regression it exists for. So the cancel waits for the announcement frame
    //     itself, observed on a spectator bus: that is the event whose confirmation arms the
    //     window, and it cannot be reached before the pending claim is registered.
    //
    //   * WHAT to assert. `NotBe(Claimed)` is satisfied by a node wedged in Claiming, which is
    //     exactly what a missing teardown leaves behind if the deadline never armed. The
    //     terminal state after a cancel inside the window is NotClaimed, so that is what is
    //     asserted -- it catches the regression through the state machine even on a run where
    //     the sequencing above lost its race, rather than relying on the timer having fired.
    //
    // Verified by restoring the regression (dropping both the teardown post and the
    // already-completed guard in OnClaimAnnounceElapsed): this test fails, as the version
    // before the timing rework did.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ClaimAddressAsync_CancelDuringArbitration_TearsDownPendingClaim()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1); // spectator: sees the announcement on the wire

        // Long arbitration window so the test can cancel comfortably in the middle. 500 ms is
        // well above the actor scheduling jitter we need to observe.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var node = J1939Node.Open(busA, opts);

        // The announcement leaving the bus is the signal that the arbitration window is about
        // to be armed -- see the note above on why the Claiming transition is too early and a
        // bounded poll loop is too late (the poll this replaces gave itself twenty
        // Task.Delay(10) hops inside a 500 ms window and overran it on a loaded runner, #92).
        var announced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (J1939Pgn.IsAddressClaim(fields.Pgn) && fields.SourceAddress == 0x33)
                announced.TrySetResult(true);
        };

        {
            using var cts = new CancellationTokenSource();
            var claimTask = node.ClaimAddressAsync(0x33, cts.Token);

            await announced.Task.WithTimeout(ShortTimeout);

            cts.Cancel();

            // The task itself must complete as cancelled. This is also the causal witness
            // that the cancel landed inside the arbitration window: had the window expired
            // first, the claim would have completed successfully instead.
            Func<Task> awaitClaim = () => claimTask.WithTimeout(ShortTimeout);
            await awaitClaim.Should().ThrowAsync<TaskCanceledException>();

            // Wait past the original arbitration window so any surviving timer would have
            // fired.
            await Task.Delay(700);

            // NotClaimed specifically, not merely "not Claimed": a missing teardown leaves the
            // node wedged in Claiming, which NotBe(Claimed) would wave through. This is the
            // assertion that catches the regression whether or not the deadline was armed.
            node.ClaimState.Should().Be(J1939ClaimState.NotClaimed);
            node.Address.Should().BeNull();

            // A fresh claim must still work (i.e. teardown left the state machine consistent).
            await node.ClaimAddressAsync(0x44).WithTimeout(ShortTimeout);
            node.ClaimState.Should().Be(J1939ClaimState.Claimed);
            node.Address.Should().Be((byte)0x44);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600614141 regression: when cts.Cancel() lands *at or after* the arbitration
    // deadline expires, OnClaimAnnounceElapsed can hit its "TCS already completed" early-
    // return branch (the token registration set TrySetCanceled before the cancel post
    // reached the actor) and the subsequent CancelPendingClaimOnLoop can then find
    // `_pendingClaim` already null. Before the fix, that pair left ClaimState stuck at
    // Claiming with no address and TP still bound to 0xFE — an unrecoverable-except-via-
    // BeginClaim state that both the caller (who saw TaskCanceled) and any observer
    // (who polls ClaimState) were told nothing about.
    //
    // The invariant this test enforces: regardless of which side of the deadline/cancel
    // race won on a given iteration, the settled ClaimState must NEVER remain at Claiming
    // (only NotClaimed or Claimed are legal terminal states after a canceled claim).
    // We use CancellationTokenSource.CancelAfter with the arbitration timeout so the two
    // timers (System.Threading.Timer for CT and DeadlineScheduler for the announce) race
    // on nearly the same wall-clock instant, spread over many iterations to sample both
    // sides of the race.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ClaimAddressAsync_CancelAtArbitrationDeadline_NeverLeavesStateStuckInClaiming()
    {
        var session = NewSession();
        using var busA = Open(session, 0);

        // Tight arbitration window so CancelAfter and the announce deadline collide with
        // minimum jitter separation. Repeated iterations vary the exact interleave
        // through natural CI scheduling jitter across ThreadPool and the actor loop.
        var arbitrationTimeout = TimeSpan.FromMilliseconds(30);
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = arbitrationTimeout,
        };
        using var node = J1939Node.Open(busA, opts);

        for (int iter = 0; iter < 40; iter++)
        {
            byte preferred = (byte)(0x30 + (iter % 0x50));
            using var cts = new CancellationTokenSource();

            // Fire the cancel via System.Threading.Timer at (approximately) the same
            // instant the arbitration deadline fires on the actor loop. That maximises
            // the chance of catching the "OnClaimAnnounceElapsed observes a canceled
            // TCS" interleave the fix targets.
            cts.CancelAfter(arbitrationTimeout);

            var claimTask = node.ClaimAddressAsync(preferred, cts.Token);
            try
            {
                await claimTask.WithTimeout(ShortTimeout);
            }
            catch (OperationCanceledException) { /* cancel raced ahead of the deadline */ }
            // If claimTask completed successfully the deadline won and we ended up
            // Claimed at `preferred`; either outcome is acceptable — the invariant we
            // care about is that ClaimState never remains at Claiming after settle.

            // Give the actor loop time to fully unwind both callbacks (Fire and cancel
            // post). The invariant is that the state *settles* out of Claiming, not that it
            // does so inside any particular number of milliseconds, so poll for it: on a
            // loaded runner the teardown can trail the awaited task by far more than the
            // deadline itself. A test that fails only because a shared runner was busy tests
            // the runner.
            var settleDeadline = DateTime.UtcNow + ShortTimeout;
            while (node.ClaimState == J1939ClaimState.Claiming && DateTime.UtcNow < settleDeadline)
                await Task.Delay(10);

            node.ClaimState.Should().NotBe(J1939ClaimState.Claiming,
                $"iteration {iter}: cancelling at the arbitration deadline must never leave the node stuck in Claiming");
            if (node.ClaimState == J1939ClaimState.NotClaimed)
                node.Address.Should().BeNull(
                    $"iteration {iter}: NotClaimed after cancel must have a null address");
        }

        // After the stress loop, the node's state machine must still be responsive: a
        // fresh uncancelled claim on a new SA must go through cleanly no matter which
        // race outcome dominated the loop above.
        await node.ClaimAddressAsync(0x50).WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x50);
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600377725 regression: starting a fresh ClaimAddressAsync on an already-claimed
    // node MUST invalidate the old SA immediately. SendAsync must reject application traffic
    // (throw J1939NoAddressException) until the new claim reaches Claimed again, otherwise the
    // node keeps transmitting with the old SA while its address-claim frame advertises a
    // different preferred one on the wire.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ReClaim_RejectsSendUntilNewClaimSucceeds()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1); // spectator: watches which SAs appear on the wire

        // Give the arbitration window enough room that we can observe the mid-claim gap even on
        // a fast Virtual bus. 500 ms is well above CI jitter but short enough to keep the test
        // fast.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var node = J1939Node.Open(busA, opts);

        // Initial claim -> we hold 0x11.
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);

        // A send with the initial claim succeeds; SA on the wire must be 0x11.
        byte? observedSa = null;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn == 0xFEF3u) observedSa = fields.SourceAddress;
        };
        await node.SendAsync(new J1939Message(0xFEF3u, new byte[] { 1, 2, 3 })).WithTimeout(ShortTimeout);
        await Task.Delay(50);
        observedSa.Should().Be((byte)0x11);

        // Start a re-claim to a different preferred SA — do NOT await yet so we can inspect
        // the mid-claim behavior. The state must transition out of Claimed immediately.
        var reclaimTask = node.ClaimAddressAsync(0x22);

        // Give the actor a beat to process BeginClaim.
        for (int i = 0; i < 20 && node.ClaimState == J1939ClaimState.Claimed; i++)
            await Task.Delay(10);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed,
            "starting a new claim must clear the previous Claimed state so old-SA traffic is gated off");
        node.Address.Should().BeNull(
            "the previous claimed address must be invalidated before the new preferred SA is announced");

        // SendAsync MUST reject application traffic while the claim is in-flight. Before the
        // fix, SendCoreAsync only checked _addressStore >= 0, so this send would silently go
        // out with the *previous* SA (0x11) while claim frames advertised 0x22.
        Func<Task> sendMidClaim = () => node.SendAsync(new J1939Message(0xFEF4u, new byte[] { 4, 5, 6 }));
        await sendMidClaim.Should().ThrowAsync<J1939NoAddressException>();

        // Once the new claim completes, application traffic MUST resume on the new SA.
        await reclaimTask.WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x22);

        byte? postSa = null;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn == 0xFEF5u) postSa = fields.SourceAddress;
        };
        await node.SendAsync(new J1939Message(0xFEF5u, new byte[] { 7, 8, 9 }))
            .WithTimeout(ShortTimeout);
        await Task.Delay(50);
        postSa.Should().Be((byte)0x22);
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600591973 behavior lock: RebindTransportOnLoop MUST dispose the previous
    // IJ1939TpChannel synchronously on the actor loop BEFORE opening a new channel. Before
    // the fix, the old channel's Dispose was fire-and-forget while a fresh channel was
    // simultaneously opened, so both channels could remain briefly subscribed to the bus
    // and both accepted broadcast TP.BAM (DA = 0xFF). Reassembly and MessageReceived then
    // fired once per surviving channel, delivering the same BAM twice.
    //
    // The pre-fix window between Open and Task.Run(Dispose) is sub-millisecond on the
    // virtual bus, so this test cannot deterministically reproduce the race on every run;
    // it pins the correctness invariant across many claim/re-claim cycles under continuous
    // broadcast BAM traffic. The synchronous-dispose fix makes the invariant hold by
    // construction.
    //
    // Two separate invariants, each carried by its own traffic, because they need opposite
    // things from the test:
    //
    //   * "never twice" needs *concurrent* traffic — a BAM must be in flight while a rebind
    //     happens for the two-live-channels window to be hit at all. That is the background
    //     stream below. It is free-running, and nothing is asserted about how much of it
    //     arrives; only that no sequence number arrives more than once.
    //
    //   * "still delivering" needs *quiet* traffic — one BAM sent after a rebind has finished,
    //     with no further rebind due until the test issues the next claim, so it cannot be
    //     straddled and its delivery is guaranteed rather than probable. That is the probe.
    //
    // Trying to get both from one free-running stream is what made this test fail on Windows CI
    // and pass everywhere else. It asserted a delivery floor of sent/2 over the background
    // stream, and how much of that stream survives is a ratio of two unrelated clocks: the
    // rebind cadence (ClaimAnnounceTimeout, 40 ms) against how long one BAM occupies the wire
    // (Th between DT frames). A BAM that straddles a rebind is dropped by the disposed channel
    // — that is by design, reassembly state is not carried across a rebind — so as the BAM
    // period approaches the rebind spacing, *every* BAM straddles one and delivery collapses.
    // Measured on this suite by stretching Th, which is what a runner with coarse timer
    // granularity does to the peer's requested 2 ms:
    //
    //     Th =  2 ms  ->  118 sent, 101 delivered, losses in 15 runs of at most 2
    //     Th = 16 ms  ->   17 sent,   2 delivered, losses in 2 runs, longest 12
    //     Th = 32 ms  ->    8 sent,   0 delivered, one run of 8
    //
    // No duplicate was ever observed, in any of those. The floor was measuring the runner, not
    // the node — so it is gone, replaced by the probe, which is exact (all 8 arrive) and holds
    // however slow the machine is.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RebindTransport_DoesNotDeliverBamMoreThanOncePerRebind()
    {
        const uint backgroundPgn = 0xFED1u;
        const uint probePgn = 0xFED2u;
        const int claims = 8;

        var session = NewSession();
        using var busPeer = Open(session, 0);
        using var busNode = Open(session, 1);
        using var busProbe = Open(session, 2);

        // Short arbitration window so many rebinds happen while peer traffic is in flight;
        // small Th so a single BAM takes a couple of ms end-to-end.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(40),
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(2)),
        };
        using var node = J1939Node.Open(busNode, opts);

        // Every delivered BAM is recorded as (PGN, sequence number), so a duplicate is
        // identifiable as such instead of only showing up as "one more than expected".
        var delivered = new List<(uint Pgn, int Seq)>();
        var deliveredGate = new object();
        TaskCompletionSource<int>? probeArrived = null;
        node.MessageReceived += (_, m) =>
        {
            if (m.Pgn != backgroundPgn && m.Pgn != probePgn) return;
            var seq = BitConverter.ToInt32(m.Payload.Span.Slice(0, 4));
            lock (deliveredGate) delivered.Add((m.Pgn, seq));
            if (m.Pgn == probePgn) probeArrived?.TrySetResult(seq);
        };

        static byte[] Datagram(int seq)
        {
            // 12 bytes, so still a genuine multi-frame BAM; the first four carry the sequence
            // number and the rest is the same filler as before.
            var payload = new byte[12];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), seq);
            for (int b = 4; b < payload.Length; b++) payload[b] = (byte)(0xE0 + b);
            return payload;
        }

        using var peerTp = CanKit.Pro.J1939Tp.J1939Tp.Open(busPeer, sourceAddress: 0x77,
            new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(2)));
        // A second source address for the probes: one SA may only run one BAM session at a
        // time, and the probe must not have to queue behind the background stream.
        using var probeTp = CanKit.Pro.J1939Tp.J1939Tp.Open(busProbe, sourceAddress: 0x78,
            new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(2)));

        int sent = 0;
        using var peerCts = new CancellationTokenSource();
        var peerTask = Task.Run(async () =>
        {
            try
            {
                while (!peerCts.IsCancellationRequested)
                {
                    await peerTp.SendBamAsync(backgroundPgn, Datagram(Volatile.Read(ref sent)), peerCts.Token)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref sent);
                }
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch { /* channel disposed during shutdown */ }
        });

        // Cycle re-claims to a new SA every iteration. Each ClaimAddressAsync triggers two
        // RebindTransportOnLoop calls (unbind to 0xFE, then rebind to the new SA) — that is
        // where the old/new channel overlap window lived pre-fix.
        for (int i = 0; i < claims; i++)
        {
            byte sa = (byte)(0x30 + i);
            await node.ClaimAddressAsync(sa).WithTimeout(ShortTimeout);
            node.ClaimState.Should().Be(J1939ClaimState.Claimed);

            // The probe, sent the moment the rebind is done. Two things ride on it: the node
            // must still receive broadcasts through the channel it just opened (asserted right
            // here, so a build that stops delivering fails at the first claim rather than in an
            // aggregate at the end), and this is also the instant a fire-and-forget-disposed
            // predecessor would still be subscribed — so a duplicate is most likely exactly
            // here, where the uniqueness check below will see it.
            probeArrived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await probeTp.SendBamAsync(probePgn, Datagram(i)).WithTimeout(ShortTimeout);
            (await probeArrived.Task.AsTaskWithTimeout(ShortTimeout)).Should().Be(i,
                "the node must still receive broadcast BAM after rebinding to a new source address");
        }

        peerCts.Cancel();
        try { await peerTask.WithTimeout(ShortTimeout); } catch { /* peer cancel/dispose */ }

        // Let any in-flight reassembly surface before the final duplicate check. This is a
        // settle for a negative assertion — there is no event that says "no duplicate is
        // coming" — so it is a delay by necessity, not by convenience.
        await Task.Delay(150);

        int finalSent = Volatile.Read(ref sent);
        List<(uint Pgn, int Seq)> got;
        lock (deliveredGate) got = new List<(uint, int)>(delivered);

        // Sanity: the background stream exists to put a BAM in flight across the rebinds, so
        // the uniqueness assertion below is only meaningful if it actually produced traffic.
        // The slowest configuration measured above still managed 8, and Windows CI 29.
        finalSent.Should().BeGreaterThan(5,
            "the background stream must generate BAM traffic across the rebind windows");

        // The actual invariant. Before the fix, the fire-and-forget Dispose of the previous
        // channel overlapped a freshly-opened one and both subscriptions delivered the same
        // reassembled datagram — which shows up here as the same (PGN, sequence) twice.
        got.Should().OnlyHaveUniqueItems(
            "no broadcast TP.BAM may be surfaced twice, and each carries its own sequence number");
        // NotContain rather than OnlyContain: how much of the background stream survives is
        // exactly what this test refuses to assert, and OnlyContain fails on an empty
        // collection — which would smuggle "at least one background BAM arrived" back in as a
        // hidden throughput assumption. Stated as a negative, it holds at any delivery rate
        // including zero, while still catching a corrupted or invented sequence number.
        //
        // The bound is <= finalSent, not <: `sent` is incremented only after SendBamAsync
        // completes, so cancelling the peer leaves the in-flight BAM uncounted while its DT
        // frames may already be on the wire. The settle above exists precisely so that
        // datagram can still reassemble, and its sequence is then equal to finalSent — a
        // cancelled send that got through, not a corrupt payload. (Carried over from
        // 2b13918, which found this on the previous form of the assertion; it applies
        // unchanged here.)
        got.Where(d => d.Pgn == backgroundPgn).Should()
            .NotContain(d => d.Seq < 0 || d.Seq > finalSent,
                "every delivered datagram must be one the peer actually sent, reassembled intact");

        // Exact, and independent of how fast the runner is: one probe per claim, all delivered.
        // Loss in the background stream is expected and deliberately not asserted on — a BAM
        // straddling a rebind is dropped by design — but a node that stops receiving after a
        // rebind cannot get all eight probes through.
        got.Where(d => d.Pgn == probePgn).Select(d => d.Seq).Should()
            .Equal(Enumerable.Range(0, claims),
                "each rebind must be followed by a delivered probe, exactly once, in order");
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600591980 regression: SendCoreAsync checks ClaimState/address once before
    // awaiting the wire I/O. A concurrent ClaimAddressAsync running on the actor loop can
    // clear or move the SA mid-flight, and before the fix the send task completed
    // successfully — the frame went out on the previous SA while the wire simultaneously
    // advertised a different preferred address (or the multi-frame session was interrupted
    // by RebindTransportOnLoop with an internal ObjectDisposedException surfacing). The
    // send MUST fail with J1939NoAddressException so the failure mode matches the pre-send
    // gate on ClaimState==Claimed.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Send_InFlightAcrossReclaim_FailsWithNoAddressException()
    {
        var session = NewSession();
        using var busA = Open(session, 0);

        // Longer Th so a multi-frame TP.BAM stays on the wire long enough for us to start a
        // re-claim while the send is still awaiting the last TP.DT.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200),
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(60)),
        };
        using var node = J1939Node.Open(busA, opts);
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);
        node.Address.Should().Be((byte)0x11);

        // Multi-frame BAM: 60 bytes → 9 TP.DT frames at Th ≈ 60 ms each keeps the send task
        // awaiting for several hundred ms, giving us room to trigger a re-claim.
        var payload = new byte[60];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var sendTask = node.SendAsync(new J1939Message(0xFED2u, payload, destinationAddress: 0xFF));

        // Wait for the actor to actually start the TP session before racing the re-claim
        // in; otherwise BeginClaim could run before SendCoreAsync captured the SA.
        for (int i = 0; i < 20 && !sendTask.IsCompleted && node.ClaimState == J1939ClaimState.Claimed; i++)
            await Task.Delay(10);

        // Kick off a reclaim to a different preferred address. BeginClaim clears the
        // captured address, and (with the Bugbot 3600591973 fix) synchronously disposes the
        // shared TP channel — either way SendCoreAsync must not report success.
        var reclaimTask = node.ClaimAddressAsync(0x22);

        Func<Task> awaitSend = () => sendTask.WithTimeout(ShortTimeout);
        await awaitSend.Should().ThrowAsync<J1939NoAddressException>(
            "an in-flight send whose captured SA was invalidated by a concurrent " +
            "ClaimAddressAsync must surface the same J1939NoAddressException as the pre-send gate");

        // The reclaim itself must still complete cleanly on the new SA — the send failure
        // does not tear down the claim state machine.
        await reclaimTask.WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x22);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-007: periodic single-frame PGN send. Every periodic PGN flows through the
    // node's SendAsync / actor loop (L2 scheduling) — the previous dual `IPeriodicTx` path
    // was collapsed to a single implementation (PR #33) so error handling and claim-gate
    // semantics are uniform across payload sizes. The test collects a run of frames on a
    // spectator bus and asserts the two rate properties that survive a loaded runner.
    //
    // What this test deliberately does NOT assert is grid alignment, and the reason is
    // measurement resolution rather than modesty. PeriodicSchedule skips a tick whose
    // previous emission is still in flight and coalesces the ticks that fell behind by
    // advancing the anchor whole periods at a time, so under load it emits at 2 x period, or
    // 3 x, *by design*. Checking that those gaps land on the grid means resolving them to
    // better than half a period -- and these stamps are taken on a spectator bus, where an
    // emission observed late next to one observed on time already moves a gap by ~50 ms.
    // Against a 120 ms period that is 40 % of the grid spacing, so the check cannot separate
    // a drifting scheduler from a busy host no matter how the tolerance is set. Measured, not
    // assumed: under an 8x CPU overload the observed gaps scatter across 130..410 ms.
    //
    // The anti-drift property is real and is tested -- by the multi-frame sibling below,
    // which earns the resolution by deriving its period from a measured send time so that one
    // period is several times the jitter. Asserting it twice, once where it cannot be
    // measured, bought a standing red leg on macOS (#92) and no coverage.
    //
    // So, the two properties that hold regardless of host load:
    //
    //   * No bursting -- every gap rounds to at least one slot. Coalescing must advance the
    //     anchor, never queue the ticks it skipped and release them back to back. Jitter
    //     cannot fake this: half a period separates a real emission from slot zero.
    //   * Never faster than requested -- the mean gap is at least one period. Dropped ticks
    //     only ever make it longer, so this bound is one-sided in the direction load pushes
    //     and cannot be tripped by a slow runner.
    //
    // The collection loop above closes the other side: requiring requiredSamples emissions
    // inside ShortTimeout bounds the mean gap from above too, loosely but honestly.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task StartPeriodicSend_SingleFrame_FiresAtConfiguredPeriod()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1); // spectator: samples arrival times

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        await sender.ClaimAddressAsync(0xC1).WithTimeout(ShortTimeout);

        // The stamp collection is protected by its own lock; the FrameObserved handler runs
        // on the bus's dispatch thread and multiple readers might in principle observe the
        // frame concurrently on some adapters.
        // Stopwatch ticks, not DateTime.UtcNow: these samples are only ever subtracted from
        // each other, and a wall clock can step under them mid-run. Same monotonic basis the
        // actor measures its own deadlines on.
        var stamps = new List<long>();
        var stampsLock = new object();
        const uint targetPgn = 0xFEE5u; // PDU2, PS=0xE5 (arbitrary), well-known-ish
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0xC1) return;
            if (fields.Pgn != targetPgn) return;
            lock (stampsLock) stamps.Add(Stopwatch.GetTimestamp());
        };

        // 120 ms period is comfortably above both the ~1 ms virtual-loopback latency and the
        // ~15.6 ms default timer granularity on Windows, and short enough to gather the
        // samples in under two seconds.
        var period = TimeSpan.FromMilliseconds(120);
        // 22, not 10. The bound below is undercut by however late the run's first emission was,
        // divided by the number of *measured* gaps -- see the assertion for why that is the error
        // term that matters. Note the two subtractions: 22 emissions give 21 gaps, and trimming
        // the warm-up leaves 20. Ten samples would divide a 240 ms cold start by nine and lose
        // 27 ms of a 120 ms period; twenty measured gaps divide it by twenty, which is what the
        // 10 % allowance below is worth. This is the knob that makes the assertion sound, so it
        // is not a free parameter.
        const int requiredSamples = 22;
        var payload = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var message = new J1939Message(targetPgn, payload, priority: 6, destinationAddress: 0xFF);

        using (var handle = sender.StartPeriodicSend(message, period))
        {
            handle.Should().NotBeNull();

            // Collect until we have enough samples for a stable mean, or bail out with a
            // clear failure message if the schedule never fires.
            // Not ShortTimeout: 21 samples at 120 ms need 2.5 s even when nothing is dropped, and
            // a loaded runner coalescing to 2x or 3x the period needs several times that. Four
            // times the nominal run is generous enough not to fail for being slow, and still
            // bounds the rate from above -- the one direction the assertion below does not cover.
            var collectBudget = TimeSpan.FromMilliseconds(period.TotalMilliseconds * requiredSamples * 4);
            var deadline = Stopwatch.GetTimestamp() + (long)(collectBudget.TotalSeconds * Stopwatch.Frequency);
            while (true)
            {
                int count;
                lock (stampsLock) count = stamps.Count;
                if (count >= requiredSamples) break;
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException(
                        $"Expected at least {requiredSamples} periodic emissions within " +
                        $"{collectBudget.TotalSeconds:F1}s; observed {count}.");
                await Task.Delay(20);
            }
        }

        // Post-Dispose: no additional frames should arrive after a settle window.
        int countAtDispose;
        lock (stampsLock) countAtDispose = stamps.Count;
        await Task.Delay(period + period); // wait 2 periods
        int countAfterSettle;
        lock (stampsLock) countAfterSettle = stamps.Count;

        countAfterSettle.Should().BeLessOrEqualTo(countAtDispose + 1,
            "disposing the handle must stop the periodic loop so at most an already-in-flight " +
            "SendAsync may still land after Dispose returns");

        List<long> snapshot;
        lock (stampsLock) snapshot = new List<long>(stamps);
        snapshot.Count.Should().BeGreaterOrEqualTo(requiredSamples);

        double targetMs = period.TotalMilliseconds;
        var gaps = new List<double>(snapshot.Count - 1);
        for (int i = 1; i < snapshot.Count; i++)
            gaps.Add((snapshot[i] - snapshot[i - 1]) * 1000d / Stopwatch.Frequency);

        // Never faster than requested, as the mean gap over a warm-up-trimmed sample.
        //
        // Three statistics have been tried here and the first two were chosen by intuition; this
        // one is chosen by its error term, which is the only way to size it honestly.
        //
        // Every emission lands on its own grid slot, late by however long the host stalled:
        // t(i) = slot(i) * period + late(i). Summing the gaps telescopes, so
        //
        //     mean gap = (slots spanned / gaps) * period + (late(last) - late(first)) / gaps
        //
        // The first term is at least the period, because slots are distinct. The second is the
        // whole problem, and it is bounded by the *number of gaps* -- nothing else. That rules
        // out both earlier attempts:
        //
        //   * The plain mean over nine gaps divides a cold start by nine. A first tick 239 ms
        //     late leaves a mean of 106.8 ms against this bound, from a scheduler doing exactly
        //     what it documents.
        //   * The median has no such error term to shrink, which looked like an advantage and is
        //     not: it is sensitive to the shape of the jitter instead of its size. On a real
        //     macOS runner the gaps came out 83, 132, 73, 173, 67, 188, 62, 105, 185 -- mean
        //     118.7 ms, so the rate was right to within 1 % -- and the median was 105 ms, because
        //     an odd number of alternating gaps has one more short than long. It failed a
        //     perfectly good run.
        //
        // So: trim the first gap, which is the only one measured from a cold schedule, and take
        // the mean of the rest. Twenty measured gaps hold the residual endpoint term under a
        // tenth of a period for any swing below 20 x 12 ms = 240 ms between the second emission's
        // lateness and the last one's -- which is exactly the worst cold start observed here, and
        // an order of magnitude beyond the jitter a loaded runner has otherwise produced.
        // Oscillation cancels in a mean by construction, so the case above passes.
        var measured = gaps.GetRange(1, gaps.Count - 1);
        var meanGap = measured.Sum() / measured.Count;

        // One-sided on purpose: load can only lengthen gaps, so there is no honest upper bound to
        // pair with this one. The collection loop bounds the rate from above instead.
        meanGap.Should().BeGreaterOrEqualTo(targetMs * 0.9,
            $"mean gap over {measured.Count} samples ({meanGap:F0} ms, first gap discarded as "
            + $"warm-up) must not undercut the configured period ({targetMs:F0} ms); "
            + "observed gaps: " + string.Join(", ", gaps.ConvertAll(g => $"{g:F0}")));
    }

    /// <summary>
    /// #92 step 2, J1939 half: the same fixed-rate property, on a clock the test drives.
    ///
    /// The wall-clock version above calibrates a period from a measured send, then allows gaps
    /// 30 % off the grid — a tolerance picked to survive the slowest runner seen so far, which is
    /// the shape #92 says gets widened again next time. Here the node schedules against a clock
    /// only this test moves, so "on the grid" is exact.
    ///
    /// <b>The trap this test had to avoid.</b> A virtual clock makes work free, and this test
    /// distinguishes anchor scheduling from a send-then-delay loop <em>only because a send takes
    /// time</em>. Two things restore that. The bus double charges virtual time for every frame it
    /// transmits, so an emission costs what a real one would; and each round advances to an
    /// absolute grid slot rather than by one period, so drift is not carried along with the
    /// goalposts. Under send-then-delay the next emission falls due a whole send-cost past the
    /// slot and so does not happen at all while the clock sits on it, which this reports as the
    /// emission never arriving.
    ///
    /// Both halves were needed, and finding that out took a wrong mutation first: rescheduling
    /// from "now" <em>inside OnTick</em> changes nothing, because OnTick runs before the send has
    /// cost anything. The hypothesis this guards against is structural — a loop that awaits the
    /// send and only then starts its delay — and only a mutation shaped like that discriminates.
    /// </summary>
    [Fact]
    public async Task StartPeriodicSend_MultiFrame_Emits_On_An_Exact_Grid_On_A_Clock_The_Test_Drives()
    {
        var period = TimeSpan.FromMilliseconds(200);
        var perFrameCost = TimeSpan.FromMilliseconds(1);
        const int requiredEmissions = 6;

        using var clock = new VirtualClock();
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        // Every transmitted frame costs virtual time, so a BAM emission is not free: a 60-byte
        // payload is one TP.CM announce plus nine TP.DT frames.
        bus.OnTransmitting = _ => clock.Advance(perFrameCost);

        var nodeOptions = new J1939NodeOptions(Name(1))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(1)),
        };
        var senderActor = clock.NewActor();
        using var sender = new J1939NodeImpl(service, nodeOptions, ownsService: false, senderActor);

        // The claim waits out its contention window on the same clock, so it needs the clock
        // moved before it can succeed -- it is a precondition here, not the subject.
        await clock.RunUntilAsync(sender.ClaimAddressAsync(0xC1),
            step: TimeSpan.FromMilliseconds(50), giveUpAfter: ShortTimeout);

        const uint targetPgn = 0xFEE6u;
        var announces = new List<TimeSpan>();
        var announcesLock = new object();
        var dataFrames = 0;
        bus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0xC1) return;
            // Data frames of the transfer: counted so a round can wait for the whole emission,
            // not just its announce. A tick arriving while the previous emission is still in
            // flight is dropped by design (one TP session per schedule), so advancing early
            // would be testing that rule instead of the grid.
            if (J1939Pgn.IsTransportDt(fields.Pgn)) { Interlocked.Increment(ref dataFrames); return; }
            if (!J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length < 8 || data[0] != J1939TpFrames.ControlBam) return;
            if (J1939TpFrames.ReadDataPgn(data) != targetPgn) return;
            lock (announcesLock) announces.Add(clock.Elapsed);
        };

        var payload = Enumerable.Range(0, 60).Select(i => (byte)(i & 0xFF)).ToArray();
        var message = new J1939Message(targetPgn, payload, priority: 6, destinationAddress: 0xFF);

        int Count() { lock (announcesLock) return announces.Count; }

        // 60 bytes over 7-byte data frames is 9 per emission.
        const int dataFramesPerEmission = 9;

        // The virtual resolution the period is bracketed to.
        var Step = TimeSpan.FromMilliseconds(1);

        var startedAt = clock.Elapsed;
        using (sender.StartPeriodicSend(message, period))
        {
            for (var slot = 1; slot <= requiredEmissions; slot++)
            {
                var slotPoint = startedAt + TimeSpan.FromTicks(period.Ticks * slot);

                // Which period the schedule is actually on is decided here, by asking the
                // actor which instant its next tick is armed for. A shorter period arms a nearer
                // one, and no jump this loop makes can turn that into a match.
                //
                // The wire cannot answer it. A jump straight to the slot passes over the earlier
                // deadline of a shorter period, Reschedule coalesces the missed anchors, and
                // exactly one emission comes out either way -- Codex found that on the first
                // revision, and halving the period confirmed it. The one-tick-short probe below
                // was the first answer and is not sufficient on its own either: SettleAsync ends
                // the actor callback, not the send it hands to the thread pool, so an early
                // emission can still be off the wire when Count() reads (Bugbot on #113).
                await clock.WaitUntilTimerArmedAsync(senderActor, slotPoint - clock.Elapsed,
                    ShortTimeout);

                // One tick short of the slot: corroboration on the wire that the tick armed above
                // has not fired early. It is the barrier, not this, that pins the period.
                await clock.AdvanceToAsync(slotPoint - Step);
                await clock.SettleAsync();
                Count().Should().Be(slot - 1,
                    "the clock is one tick short of slot {0}, so that emission is not due yet",
                    slot);

                await clock.AdvanceToAsync(slotPoint);
                await WaitForAnnouncesAsync(Count, slot);
                await WaitForAnnouncesAsync(() => Volatile.Read(ref dataFrames),
                    slot * dataFramesPerEmission);

                // The last data frame on the wire is not the end of the emission: OnTick drops a
                // tick whose predecessor is still in flight, and that state clears on the
                // transport's own loop. Waiting for the node to say so is what keeps the next
                // slot's assertion about the grid rather than about a race (Bugbot on #113).
                await WaitForAnnouncesAsync(() => sender.PeriodicEmissionsCompleted, slot);
            }
        }

        List<TimeSpan> snapshot;
        lock (announcesLock) snapshot = new List<TimeSpan>(announces);

        // Every announce sits on its slot. The send cost that accrued before it is exactly what a
        // send-then-delay loop would have added to the next due point, and it does not move these.
        for (var i = 0; i < requiredEmissions; i++)
        {
            var slotPoint = startedAt + TimeSpan.FromTicks(period.Ticks * (i + 1));
            snapshot[i].Should().BeGreaterThanOrEqualTo(slotPoint,
                "emission {0} is triggered by the clock reaching its slot", i + 1);
            snapshot[i].Should().BeLessThan(slotPoint + period,
                "emission {0} belongs to slot {0} and not to a later one: a schedule that "
                + "restarted its period after each send would have drifted past it", i + 1);
        }
    }

    // #95 -- a node must not raise its own transmissions as peer traffic. Run in both echo
    // worlds (#94) because the two answers to "does the adapter flag its echo?" reach the guard
    // by different routes, and the default test adapter only reaches one of them.
    //
    // The guard is an unconditional source-address check on application PGNs, matching what
    // J1939TpChannel already does. It sits after the Address Claim branch in HandleIncomingFrame,
    // so arbitration -- which runs on PGN 0xEE00 and is what has to see a peer wrongly using our
    // address -- is untouched by it. That is what dissolves the trade-off #95 left open: the
    // conjunction with IsEcho would have been precise on a flagging adapter and inert on every
    // other one.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_Node_Does_Not_Raise_Its_Own_Broadcast_As_Peer_Traffic(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        const uint ownPgn = 0xFEF3u;
        const uint peerPgn = 0xFEF4u;

        // Our own broadcast first, the peer's second, both on the one bus this node reads. The
        // order is the barrier: a single subscription delivers in arrival order, so once the
        // peer's message has been raised, ours has already been through the reader and either
        // dropped or delivered. No wait for an absence, and nothing timing-dependent.
        await node.SendAsync(new J1939Message(ownPgn, new byte[] { 1, 2, 3 },
            destinationAddress: J1939Pgn.GlobalAddress)).WithTimeout(ShortTimeout);

        var peerFrame = CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, peerPgn, sourceAddress: 0x22),
            new byte[] { 4, 5, 6 }, isExtendedFrame: true);
        echo.InjectPeerFrame(peerFrame);

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == peerPgn)) break; }
            await Task.Delay(5);
        }

        List<J1939Message> snapshot;
        lock (seenLock) snapshot = new List<J1939Message>(seen);

        snapshot.Should().Contain(m => m.Pgn == peerPgn,
            "a peer's broadcast must still be delivered -- the guard drops our own source "
            + "address, not everything that arrives");
        snapshot.Should().NotContain(m => m.SourceAddress == 0x11,
            "the node sent this itself, and an echo bus hands it straight back");
    }

    // #119, found by Codex on the guard above. BeginClaimRound calls WriteAddress(null) before
    // announcing the new preferred SA, so for the whole arbitration window the node has no
    // address and the guard's `myAddr >= 0` is false. A frame this node transmitted under the old
    // SA, whose echo is still queued behind the claim on the actor, then walks straight past it.
    //
    // Reproduced deterministically rather than by racing the reader: a frame carrying the vacated
    // SA is injected *while* the claim is pending, which is the state the race produces. The
    // arbitration window is deliberately long here -- the assertion is that something is dropped
    // during the window, so a wider window can only make the test surer.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_Vacated_Address_Is_Still_Recognised_As_Ours_While_Reclaiming(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        var reclaim = node.ClaimAddressAsync(0x22);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed,
            "the rest of this test is about the window where the node has no address");

        const uint vacatedPgn = 0xFEF3u;
        const uint thirdPartyPgn = 0xFEF4u;
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, vacatedPgn, sourceAddress: 0x11),
            new byte[] { 1, 2, 3 }, isExtendedFrame: true));
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, thirdPartyPgn, sourceAddress: 0x33),
            new byte[] { 4, 5, 6 }, isExtendedFrame: true));

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == thirdPartyPgn)) break; }
            await Task.Delay(5);
        }

        List<J1939Message> snapshot;
        lock (seenLock) snapshot = new List<J1939Message>(seen);

        snapshot.Should().Contain(m => m.Pgn == thirdPartyPgn,
            "an unrelated peer must still be heard during a re-claim");
        snapshot.Should().NotContain(m => m.SourceAddress == 0x11,
            "0x11 is the address this node is in the middle of giving up, so a frame carrying it "
            + "is its own echo draining behind the claim");

        await reclaim.WithTimeout(ShortTimeout);
    }

    // #119, found by Codex and Bugbot independently on the memory added for the first finding:
    // BeginClaimRound calls WriteAddress(null) on *every* round, so a second round while still
    // Claiming -- an arbitrary-address fallback, or a replacing ClaimAddressAsync -- copied the
    // already-cleared -1 over the remembered address and forgot it.
    //
    // Driven through the public API rather than by reaching into the state: claim, start a
    // re-claim, then replace that claim while it is still in flight. The second BeginClaimRound
    // is the one that used to wipe the memory.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_Vacated_Address_Survives_A_Second_Claim_Round(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        // First round: vacates 0x11.
        var superseded = node.ClaimAddressAsync(0x22);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        // Second round while the first is still in flight. The address store is already -1 here,
        // which is exactly the case that used to overwrite the memory of 0x11.
        var reclaim = node.ClaimAddressAsync(0x23);
        Func<Task> awaitSuperseded = () => superseded.WithTimeout(ShortTimeout);
        await awaitSuperseded.Should().ThrowAsync<TaskCanceledException>();

        const uint vacatedPgn = 0xFEF7u;
        const uint thirdPartyPgn = 0xFEF8u;
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, vacatedPgn, sourceAddress: 0x11),
            new byte[] { 1, 2, 3 }, isExtendedFrame: true));
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, thirdPartyPgn, sourceAddress: 0x33),
            new byte[] { 4, 5, 6 }, isExtendedFrame: true));

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == thirdPartyPgn)) break; }
            await Task.Delay(5);
        }

        List<J1939Message> snapshot;
        lock (seenLock) snapshot = new List<J1939Message>(seen);
        snapshot.Should().Contain(m => m.Pgn == thirdPartyPgn,
            "an unrelated peer must still be heard across claim rounds");
        snapshot.Should().NotContain(m => m.SourceAddress == 0x11,
            "0x11 is still the address this node is giving up; a further claim round does not "
            + "make its own draining echo somebody else's traffic");

        await reclaim.WithTimeout(ShortTimeout);
    }

    // #119, Codex again on the same condition: while this node is claiming B, the address A it
    // gave up is free, and a peer may legitimately claim it and start broadcasting before B's
    // arbitration ends. HandleIncomingAddressClaim ignores that claim -- it only arbitrates
    // against the pending preferred address -- so the vacated marker kept discarding the new
    // owner's traffic for the rest of an unrelated claim window.
    //
    // The peer's Address Claim is the signal that A is somebody else's now, so it clears the
    // marker.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_Vacated_Address_Stops_Being_Ours_Once_A_Peer_Claims_It(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        var reclaim = node.ClaimAddressAsync(0x22);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        var peerName = Name(0x00ABCD);
        byte[] Claim(byte sa) => BitConverter.GetBytes(peerName.Value);
        void Inject(uint pgn, byte sa, byte[] data) => echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, pgn, sourceAddress: sa), data, isExtendedFrame: true));

        async Task DrainUntilAsync(uint barrierPgn)
        {
            var until = DateTime.UtcNow + ShortTimeout;
            while (DateTime.UtcNow < until)
            {
                lock (seenLock) { if (seen.Any(m => m.Pgn == barrierPgn)) return; }
                await Task.Delay(5);
            }
        }

        const uint beforePgn = 0xFEF9u;
        const uint afterPgn = 0xFEFCu;
        const uint barrier1 = 0xFEFDu;
        const uint barrier2 = 0xFEFEu;

        // First a claim for an unrelated address. It must leave the marker alone -- otherwise any
        // arbitration traffic on the bus would reopen the window this node still needs.
        Inject(J1939Pgn.AddressClaimed, 0x44, Claim(0x44));
        Inject(beforePgn, 0x11, new byte[] { 7, 7, 7 });
        Inject(barrier1, 0x33, new byte[] { 1 });
        await DrainUntilAsync(barrier1);

        lock (seenLock)
        {
            seen.Should().Contain(m => m.Pgn == barrier1);
            seen.Should().NotContain(m => m.Pgn == beforePgn,
                "a claim for some other address says nothing about the one this node is giving up");
        }

        // Now the claim for the vacated address itself.
        Inject(J1939Pgn.AddressClaimed, 0x11, Claim(0x11));
        Inject(afterPgn, 0x11, new byte[] { 8, 8, 8 });
        Inject(barrier2, 0x33, new byte[] { 2 });
        await DrainUntilAsync(barrier2);

        lock (seenLock)
        {
            seen.Should().Contain(m => m.Pgn == barrier2);
            seen.Should().Contain(m => m.Pgn == afterPgn && m.SourceAddress == 0x11,
                "a peer announced that address as its own, so it is no longer this node's to "
                + "mistake for a draining echo -- even though this node's claim is still in flight");
        }

        await reclaim.WithTimeout(ShortTimeout);
    }

    // The placement of that clear, which is load-bearing and was not covered until this test.
    // It sits *after* the NAME check, so only a peer's claim ends the marker. Re-claiming the
    // same address is where that matters: the node announces for 0x11 while 0x11 is exactly what
    // it vacated, so its own claim echo arrives carrying the marked address. Clearing on it would
    // reopen the window for this node's own draining application traffic.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task Our_Own_Claim_Echo_Does_Not_Clear_The_Vacated_Marker(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        // Re-claim the same address: the announce goes out under 0x11, and on an echo bus it
        // comes straight back carrying our own NAME and the very address we just vacated.
        var reclaim = node.ClaimAddressAsync(0x11);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        const uint vacatedPgn = 0xFEFAu;
        const uint thirdPartyPgn = 0xFEFBu;
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, vacatedPgn, sourceAddress: 0x11),
            new byte[] { 1, 2, 3 }, isExtendedFrame: true));
        echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, thirdPartyPgn, sourceAddress: 0x33),
            new byte[] { 4, 5, 6 }, isExtendedFrame: true));

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == thirdPartyPgn)) break; }
            await Task.Delay(5);
        }

        List<J1939Message> snapshot;
        lock (seenLock) snapshot = new List<J1939Message>(seen);
        snapshot.Should().Contain(m => m.Pgn == thirdPartyPgn);
        snapshot.Should().NotContain(m => m.SourceAddress == 0x11,
            "this node's own Address Claim does not hand its address to somebody else, so the "
            + "marker must survive it");

        await reclaim.WithTimeout(ShortTimeout);
    }

    // The other half of the guard above: the memory of a vacated address must expire. Without the
    // claim-in-flight condition it does not, and a node that lost its address goes deaf for good
    // to whoever now holds it -- which is worse than the defect the memory fixes, and which
    // nothing caught until this test existed.
    //
    // Two real nodes rather than the echo fixture, because the state this needs is a node
    // *unseated by contention*: address cleared, claim no longer in flight, and the peer that won
    // now transmitting from that very address.
    [Fact]
    public async Task A_Vacated_Address_Stops_Being_Ours_Once_The_Claim_Is_Over()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        const byte contended = 0x40;
        using var owner = J1939Node.Open(busA, new J1939NodeOptions(Name(0x000200))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        });
        // Lower NAME wins arbitration, so this peer takes the address off the owner.
        using var winner = J1939Node.Open(busB, new J1939NodeOptions(Name(0x000010))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        });

        await owner.ClaimAddressAsync(contended).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        owner.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        await winner.ClaimAddressAsync(contended).WithTimeout(ShortTimeout);
        var lost = DateTime.UtcNow + ShortTimeout;
        while (owner.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < lost)
            await Task.Delay(5);
        owner.ClaimState.Should().NotBe(J1939ClaimState.Claimed);
        owner.Address.Should().BeNull();

        const uint pgn = 0xFEF6u;
        await winner.SendAsync(new J1939Message(pgn, new byte[] { 9, 9, 9 },
            destinationAddress: J1939Pgn.GlobalAddress)).WithTimeout(ShortTimeout);

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == pgn)) break; }
            await Task.Delay(5);
        }

        List<J1939Message> snapshot;
        lock (seenLock) snapshot = new List<J1939Message>(seen);
        snapshot.Should().Contain(m => m.Pgn == pgn && m.SourceAddress == contended,
            "arbitration is over and this address belongs to the peer that won it; the unseated "
            + "node must hear its traffic rather than keep mistaking it for its own echo");
    }

    // #119, Codex and Bugbot on the same lines: the clear that answers the round above fired on
    // *any* peer claim for the vacated address, including one this node is contesting and
    // winning. Re-claiming the same address makes the vacated address and the pending preferred
    // address one and the same, so a peer that loses that contest took the marker down with it --
    // and the node stayed Claiming with no address, i.e. with neither guard able to fire.
    //
    // A peer that loses has taken nothing, so the marker stays.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task Winning_A_Same_Address_Contest_Keeps_The_Vacated_Marker(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        // Re-claiming the *same* address, which is what makes the two addresses coincide.
        var reclaim = node.ClaimAddressAsync(0x11);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().Be(J1939ClaimState.Claiming);

        void Inject(uint pgn, byte sa, byte[] data) => echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, pgn, sourceAddress: sa), data, isExtendedFrame: true));

        // A numerically higher NAME contests 0x11 and loses (SAE J1939-81 4.4.3.2).
        Inject(J1939Pgn.AddressClaimed, 0x11, BitConverter.GetBytes(Name(0x00ABCD).Value));

        const uint drainingPgn = 0xFEF9u;
        const uint barrierPgn = 0xFEFDu;
        Inject(drainingPgn, 0x11, new byte[] { 7, 7, 7 });
        Inject(barrierPgn, 0x33, new byte[] { 1 });

        var until = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < until)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == barrierPgn)) break; }
            await Task.Delay(5);
        }

        lock (seenLock)
        {
            seen.Should().Contain(m => m.Pgn == barrierPgn);
            seen.Should().NotContain(m => m.Pgn == drainingPgn,
                "the peer lost the contest, so 0x11 is still the address this node is re-taking "
                + "and its own traffic under it may still be draining");
        }

        // The contest was won, not lost -- otherwise the assertion above would hold for the
        // wrong reason.
        node.ClaimState.Should().Be(J1939ClaimState.Claiming);
    }

    // #119, Codex once more, this time about how long the marker may live at all. The unseated
    // path calls WriteAddress(null) while the address store still holds the lost address, so the
    // marker is set for an address that now belongs to the peer that won it. CannotClaim makes
    // the guard inert, which hides it -- until a later ClaimAddressAsync for some other address
    // sets Claiming again and brings the stale marker back with it.
    //
    // The marker is scoped to one claim sequence, and SetClaimState is where every exit from a
    // sequence passes.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task An_Address_Lost_To_A_Peer_Is_Not_Remembered_By_A_Later_Claim(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = J1939Node.Open(echo.Bus, new J1939NodeOptions(Name(0x00ABCD))
        {
            ClaimAnnounceTimeout = TimeSpan.FromSeconds(2),
        });
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);

        var seen = new List<J1939Message>();
        var seenLock = new object();
        node.MessageReceived += (_, m) => { lock (seenLock) seen.Add(m); };

        void Inject(uint pgn, byte sa, byte[] data) => echo.InjectPeerFrame(CanFrame.Classic(
            (int)J1939Id.ComposePgn(6, pgn, sourceAddress: sa), data, isExtendedFrame: true));

        // A numerically lower NAME unseats this node at 0x11; from here on that address is the
        // peer's.
        Inject(J1939Pgn.AddressClaimed, 0x11, BitConverter.GetBytes(Name(1).Value));
        var unseated = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < unseated)
            await Task.Delay(5);
        node.ClaimState.Should().Be(J1939ClaimState.CannotClaim);

        // A later, unrelated claim. It has nothing to do with 0x11 and must not resurrect it.
        var reclaim = node.ClaimAddressAsync(0x22);
        var claiming = DateTime.UtcNow + ShortTimeout;
        while (node.ClaimState != J1939ClaimState.Claiming && DateTime.UtcNow < claiming)
            await Task.Delay(5);
        node.ClaimState.Should().Be(J1939ClaimState.Claiming);

        const uint ownerPgn = 0xFEF9u;
        const uint barrierPgn = 0xFEFDu;
        Inject(ownerPgn, 0x11, new byte[] { 8, 8, 8 });
        Inject(barrierPgn, 0x33, new byte[] { 1 });

        var until = DateTime.UtcNow + ShortTimeout;
        while (DateTime.UtcNow < until)
        {
            lock (seenLock) { if (seen.Any(m => m.Pgn == barrierPgn)) break; }
            await Task.Delay(5);
        }

        lock (seenLock)
        {
            seen.Should().Contain(m => m.Pgn == barrierPgn);
            seen.Should().Contain(m => m.Pgn == ownerPgn && m.SourceAddress == 0x11,
                "0x11 was lost to a peer two claim sequences ago; a fresh arbitration for some "
                + "other address does not make that peer's traffic this node's own echo");
        }
    }

    // #113 -- the same ownership contract as the ISO-TP channel, at both of the node's exits.
    // A node opens its transport channel first and subscribes for itself second, so failing the
    // first Subscribe and failing the second reach different catch blocks; both had never run.
    // Codecov is what noticed, after I had asserted the rule repeatedly in review.
    //
    // Both halves are observed, and the second one only because Codex pointed out that the first
    // revision could not fail: it watched a subscription count that is zero whether or not the
    // node's own actor was disposed. RunningLoopCount is what a leaked actor moves.
    [Theory]
    // failOnCall 1 is the transport channel's own subscription, reaching the first catch;
    // 2 is the node's, reaching the second, which also disposes the transport it had opened.
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task A_Failed_Construction_Never_Disposes_An_Injected_Actor(
        int failOnCall, bool inject)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var inner = new CanBusService(bus);

        using var clock = new VirtualClock();
        var injected = inject ? clock.NewActor() : null;
        var failing = new ThrowingSubscribeService(inner, failOnCall);

        // Taken after the injected actor exists, so it is the baseline the failed construction
        // must come back to whichever row this is.
        var loopsBefore = ProtocolActor.RunningLoopCount;

        Action construct = () => new J1939NodeImpl(failing, new J1939NodeOptions(Name(1)),
            ownsService: false, injected);
        construct.Should().Throw<InvalidOperationException>();

        failing.SubscribeCalls.Should().Be(failOnCall,
            "the construction must have reached exactly the subscription this case fails");
        inner.SubscriptionCount.Should().Be(0,
            "a construction that failed must not leave a subscription behind");

        // Dispose joins the loop before returning, so no wait belongs here: a count still above
        // the baseline is a leak, not a loop that has yet to notice.
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore,
            "every actor the failed construction created -- the node's own and the transport "
            + "channel's -- must be disposed on the way out");

        if (injected is not null)
            (await injected.PostAsync(() => 42).WaitAsync(ShortTimeout)).Should().Be(42,
                "an injected actor belongs to the caller and must survive a failed construction");
    }

    /// <summary>
    /// Waits for a frame count to reach <paramref name="target"/>. A wait for an effect, not an
    /// assertion about how long it took: a slow runner delays this rather than failing it.
    /// </summary>
    private static async Task WaitForAnnouncesAsync(Func<int> count, int target)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (count() < target)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {target} frames, saw {count()}.");
            await Task.Delay(5);
        }
    }

    // The single-frame periodic path MUST refuse to start before ClaimAddressAsync completes,
    // mirroring SendAsync's pre-flight gate (Bugbot 3600377725) so no periodic traffic leaks
    // out with an invalid SA.
    [Fact]
    public async Task StartPeriodicSend_SingleFrame_BeforeClaim_ThrowsNoAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);

        using var node = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        var message = new J1939Message(0xFEE6u, new byte[] { 1, 2, 3 }, priority: 6,
            destinationAddress: 0xFF);

        Action act = () => node.StartPeriodicSend(message, TimeSpan.FromMilliseconds(50));
        act.Should().Throw<J1939NoAddressException>();

        // A subsequent successful claim + StartPeriodicSend must work.
        await node.ClaimAddressAsync(0xC2).WithTimeout(ShortTimeout);
        using var handle = node.StartPeriodicSend(message, TimeSpan.FromMilliseconds(80));
        handle.Should().NotBeNull();
    }

    // Bugbot 3603876664 regression: once the owning node loses its claim (a higher-priority
    // peer unseats it and it transitions to CannotClaim), the periodic schedule MUST stop
    // putting stale-SA frames on the wire. With the unified SendAsync path (PR #33),
    // SendAsync's pre-flight claim gate throws J1939NoAddressException on every subsequent
    // tick, so no CAN frame is emitted while the node is un-claimed. Assert the wire goes
    // quiet after unseating.
    [Fact]
    public async Task StartPeriodicSend_SingleFrame_StopsAfterAddressLoss()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2); // spectator: counts periodic emissions

        // Owner has a HIGHER numeric NAME → lower priority → will be unseated when the
        // peer with a lower NAME claims the same SA per SAE J1939-81 §4.4.3.2.
        var ownerOpts = new J1939NodeOptions(Name(0x000200))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };
        var peerOpts = new J1939NodeOptions(Name(0x000010))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };

        using var owner = J1939Node.Open(busA, ownerOpts);
        using var peer = J1939Node.Open(busB, peerOpts);

        const byte contendedSa = 0x50;
        await owner.ClaimAddressAsync(contendedSa).WithTimeout(ShortTimeout);
        owner.ClaimState.Should().Be(J1939ClaimState.Claimed);
        owner.Address.Should().Be(contendedSa);

        // Watch for the periodic PGN on the spectator bus so the schedule's "still emitting"
        // assertion is independent of the owner node's internal state and matches what
        // downstream ECUs actually observe.
        const uint targetPgn = 0xFEE7u;
        var stamps = new List<DateTime>();
        var stampsLock = new object();
        busC.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn != targetPgn) return;
            if (fields.SourceAddress != contendedSa) return;
            lock (stampsLock) stamps.Add(DateTime.UtcNow);
        };

        var period = TimeSpan.FromMilliseconds(40);
        var message = new J1939Message(targetPgn, new byte[] { 0xA1, 0xA2 }, priority: 6,
            destinationAddress: 0xFF);
        using var handle = owner.StartPeriodicSend(message, period);

        // Wait until the schedule has actually put a few frames on the wire so the
        // "stop" assertion below is meaningful (the schedule really was running).
        var readyDeadline = DateTime.UtcNow + ShortTimeout;
        while (true)
        {
            int c;
            lock (stampsLock) c = stamps.Count;
            if (c >= 3) break;
            if (DateTime.UtcNow >= readyDeadline)
                throw new TimeoutException("Expected ≥3 periodic frames from owner before contest.");
            await Task.Delay(10);
        }

        // Peer with lower NAME claims the same SA. HandleIncomingAddressClaim's
        // "already claimed at SA + peer wins" branch flips the owner to CannotClaim and
        // clears its address, so subsequent SendAsync calls from the periodic loop fail
        // fast at the claim gate — no more frames go out under the previous SA.
        await peer.ClaimAddressAsync(contendedSa).WithTimeout(ShortTimeout);
        peer.Address.Should().Be(contendedSa);

        // Wait for the owner's state machine to observe the contest.
        var lossDeadline = DateTime.UtcNow + ShortTimeout;
        while (owner.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < lossDeadline)
            await Task.Delay(10);
        owner.ClaimState.Should().NotBe(J1939ClaimState.Claimed);
        owner.Address.Should().BeNull();

        // Give the schedule ~2 periods to observe the state transition and let the
        // in-flight SendAsync (if any) drain. Peer traffic on `contendedSa` is filtered by
        // NAME (owner's Name(0x200) ≠ peer's Name(0x010)), so any frames on `contendedSa`
        // that arrive here originate from the owner's periodic loop *not yet stopping* —
        // that is exactly the bug we are guarding against.
        int countAfterLoss;
        lock (stampsLock) countAfterLoss = stamps.Count;
        await Task.Delay(period + period + TimeSpan.FromMilliseconds(50));
        int countAfterQuiet;
        lock (stampsLock) countAfterQuiet = stamps.Count;

        // We tolerate at most one already-in-flight emission slipping past the state
        // transition. Anything more means the loop kept sending under a stale SA.
        (countAfterQuiet - countAfterLoss).Should().BeLessOrEqualTo(1,
            "the periodic loop must stop putting frames on the wire within ~2 periods " +
            "after the owner loses its claim; otherwise stale-SA frames would keep going " +
            "out under the previous address (Bugbot 3603876664)");
    }

    // Bugbot 3604386825 regression: send failures inside the periodic loop MUST reach the
    // application via BackgroundExceptionOccurred. Now that every periodic PGN uses the
    // SendAsync-based PeriodicSchedule (PR #33), that means: after the owner loses its
    // claim, SendAsync's pre-flight gate throws J1939NoAddressException on the next tick
    // and the schedule surfaces the exception. The earlier dual-path implementation had a
    // silent-error hole when the L1 fallback swallowed Transmit exceptions; this test
    // guards against that regression coming back.
    [Fact]
    public async Task StartPeriodicSend_SingleFrame_SurfacesSendErrors()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        var ownerOpts = new J1939NodeOptions(Name(0x000200))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };
        var peerOpts = new J1939NodeOptions(Name(0x000010))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };

        using var owner = J1939Node.Open(busA, ownerOpts);
        using var peer = J1939Node.Open(busB, peerOpts);

        var backgroundExceptions = new List<Exception>();
        var exLock = new object();
        owner.BackgroundExceptionOccurred += (_, ex) =>
        {
            lock (exLock) backgroundExceptions.Add(ex);
        };

        const byte contendedSa = 0x53;
        await owner.ClaimAddressAsync(contendedSa).WithTimeout(ShortTimeout);
        owner.Address.Should().Be(contendedSa);

        var period = TimeSpan.FromMilliseconds(40);
        var message = new J1939Message(0xFEE9u, new byte[] { 0xC1, 0xC2 }, priority: 6,
            destinationAddress: 0xFF);
        using var handle = owner.StartPeriodicSend(message, period);

        // Peer unseats the owner → SendAsync's claim gate starts throwing
        // J1939NoAddressException on every scheduled emission. PeriodicSchedule.LoopAsync
        // catches non-cancellation exceptions and forwards them to
        // BackgroundExceptionOccurred so applications observe the failure.
        await peer.ClaimAddressAsync(contendedSa).WithTimeout(ShortTimeout);
        var lossDeadline = DateTime.UtcNow + ShortTimeout;
        while (owner.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < lossDeadline)
            await Task.Delay(10);
        owner.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        // Give the schedule a few periods to attempt emissions under the lost claim.
        var seenDeadline = DateTime.UtcNow + ShortTimeout;
        while (true)
        {
            bool seen;
            lock (exLock) seen = backgroundExceptions.Exists(e => e is J1939NoAddressException);
            if (seen) break;
            if (DateTime.UtcNow >= seenDeadline)
                throw new TimeoutException(
                    "Expected the schedule to surface J1939NoAddressException via " +
                    "BackgroundExceptionOccurred after address loss — periodic send " +
                    "errors must not be silently swallowed (Bugbot 3604386825).");
            await Task.Delay(10);
        }
    }

    // Optional coverage for the reclaim-with-new-SA path: after the schedule stops on
    // address loss, a subsequent successful claim (potentially on a different SA) MUST
    // re-arm the periodic emission and the wire ID MUST carry the new SA.
    [Fact]
    public async Task StartPeriodicSend_SingleFrame_ReclaimResumesUnderNewSa()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2); // spectator

        var ownerOpts = new J1939NodeOptions(Name(0x000200))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };
        var peerOpts = new J1939NodeOptions(Name(0x000010))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(80),
        };

        using var owner = J1939Node.Open(busA, ownerOpts);
        using var peer = J1939Node.Open(busB, peerOpts);

        const byte firstSa = 0x51;
        const byte secondSa = 0x52;
        await owner.ClaimAddressAsync(firstSa).WithTimeout(ShortTimeout);

        const uint targetPgn = 0xFEE8u;
        var newSaStamps = 0;
        busC.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn != targetPgn) return;
            if (fields.SourceAddress == secondSa) Interlocked.Increment(ref newSaStamps);
        };

        var period = TimeSpan.FromMilliseconds(40);
        var message = new J1939Message(targetPgn, new byte[] { 0xB1 }, priority: 6,
            destinationAddress: 0xFF);
        using var handle = owner.StartPeriodicSend(message, period);

        // Peer contests the first SA; owner is unseated → schedule tears down.
        await peer.ClaimAddressAsync(firstSa).WithTimeout(ShortTimeout);
        var lossDeadline = DateTime.UtcNow + ShortTimeout;
        while (owner.ClaimState == J1939ClaimState.Claimed && DateTime.UtcNow < lossDeadline)
            await Task.Delay(10);
        owner.ClaimState.Should().NotBe(J1939ClaimState.Claimed);

        // Owner reclaims on a different SA. The SendAsync path composes the 29-bit ID
        // from the currently-claimed SA on every tick, so the periodic emission naturally
        // resumes under the new SA without any explicit rebind.
        await owner.ClaimAddressAsync(secondSa).WithTimeout(ShortTimeout);
        owner.Address.Should().Be(secondSa);

        // Wait for a handful of frames under the new SA to confirm the schedule resumed.
        var readyDeadline = DateTime.UtcNow + ShortTimeout;
        while (Volatile.Read(ref newSaStamps) < 3 && DateTime.UtcNow < readyDeadline)
            await Task.Delay(10);
        Volatile.Read(ref newSaStamps).Should().BeGreaterOrEqualTo(3,
            "the schedule must resume under the reclaimed SA so downstream ECUs continue " +
            "to observe the PGN under the new (correct) source address");
    }
}

internal static class J1939NodeTestExtensions
{
    public static async Task<T> AsTaskWithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        return await task;
    }

    public static async Task WithTimeout(this Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        await task;
    }
}
