using System;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.IsoTp;
using CanKit.Pro.J1939;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Uds;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// NFR-006 / arc42 ADR-12: every L3/L4 protocol exception derives from <see cref="CanKitException"/>
/// and maps to its documented 6xxx error code, so library-wide failures can be caught and
/// classified uniformly instead of through six package-local hierarchies.
/// </summary>
public class Nfr006ErrorArchitectureTests
{
    [Fact]
    public void All_Protocol_Exceptions_Derive_From_CanKitException()
    {
        var exceptionTypes = new[]
        {
            typeof(IsoTpException), typeof(IsoTpTimeoutException), typeof(IsoTpOverflowException),
            typeof(IsoTpWaitFrameLimitExceededException), typeof(IsoTpSendRejectedException),
            typeof(J1939TpException), typeof(J1939TpAbortException), typeof(J1939TpSendRejectedException),
            typeof(UdsException), typeof(UdsNegativeResponseException), typeof(UdsTimeoutException),
            typeof(UdsProtocolException),
            typeof(J1939NodeException), typeof(J1939NoAddressException), typeof(J1939CannotClaimException),
            typeof(SdoAbortException), typeof(CanOpenTransportException),
        };

        foreach (var t in exceptionTypes)
        {
            typeof(CanKitException).IsAssignableFrom(t).Should().BeTrue(
                $"{t.Name} must derive from CanKitException (ADR-12 / NFR-006)");
        }
    }

    [Fact]
    public void Protocol_Exceptions_Map_To_The_Documented_ErrorCodes()
    {
        (new IsoTpTimeoutException(IsoTpTimer.NBs, "x"))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolTimeout);
        (new IsoTpOverflowException("x"))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        (new IsoTpWaitFrameLimitExceededException(received: 3, limit: 2))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        (new IsoTpSendRejectedException("x"))
            .ErrorCode.Should().Be(CanKitErrorCode.TransportOperationFailed);

        (new J1939TpAbortException(J1939TpAbortReason.Timeout, 0xEE00, "x"))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolTimeout);
        (new J1939TpAbortException(J1939TpAbortReason.UnexpectedCtsNumPackets, 0xEE00, "x"))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        (new J1939TpSendRejectedException("x"))
            .ErrorCode.Should().Be(CanKitErrorCode.TransportOperationFailed);

        (new UdsTimeoutException(UdsServiceId.ReadDataByIdentifier,
                UdsTimeoutTimer.P2Star, TimeSpan.FromMilliseconds(1)))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolTimeout);
        (new UdsNegativeResponseException(UdsServiceId.ReadDataByIdentifier, 0x31))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolNegativeResponse);
        (new UdsProtocolException("x"))
            .ErrorCode.Should().Be(CanKitErrorCode.TransportOperationFailed);

        (new J1939NoAddressException())
            .ErrorCode.Should().Be(ProtocolErrorCodes.AddressClaimFailed);
        (new J1939CannotClaimException(0x42))
            .ErrorCode.Should().Be(ProtocolErrorCodes.AddressClaimFailed);

        (new SdoAbortException(0x1000, 0x01, SdoAbortCode.General))
            .ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        (new CanOpenTransportException("x"))
            .ErrorCode.Should().Be(CanKitErrorCode.TransportOperationFailed);
    }

    // ProtocolErrorCodes deliberately occupies CanKitErrorCode 6002..6005, continuing the 6000
    // range CanKit reserves for transport and protocol errors but does not populate past 6001.
    // docs/upstream-candidates.md explains why: the numbers are the ones an upstream adoption of
    // these four codes would produce, so adopting them there deletes that file and changes nothing
    // else.
    //
    // What makes that a bet rather than a plan is that CanKit could define one of those numbers
    // for something of its own. Nothing would break loudly if it did -- ProtocolTimeout would
    // simply start rendering an unrelated upstream name, and two different failures would compare
    // equal. This is the tripwire: it fails on the CanKit bump that claims one of them, while it
    // is still a one-line version change under review rather than a mystery in a released package.
    [Fact]
    public void The_Borrowed_Protocol_Error_Codes_Are_Still_Unclaimed_Upstream()
    {
        var borrowed = new[]
        {
            ProtocolErrorCodes.ProtocolTimeout,
            ProtocolErrorCodes.ProtocolPeerAbort,
            ProtocolErrorCodes.ProtocolNegativeResponse,
            ProtocolErrorCodes.AddressClaimFailed,
        };

        foreach (var code in borrowed)
        {
            Enum.IsDefined(typeof(CanKitErrorCode), code).Should().BeFalse(
                $"CanKitErrorCode {(int)code} is claimed by the CanKit version this build pins, so " +
                "ProtocolErrorCodes now collides with it. Either upstream adopted these four codes " +
                "-- in which case ProtocolErrorCodes and docs/upstream-candidates.md § 1 go away and " +
                "callers switch to the enum members -- or it took the number for something else, and " +
                "these four move to a range it does not use.");
        }
    }
}
