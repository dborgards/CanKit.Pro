using System;

namespace CanKit.Pro.Uds;

/// <summary>What <see cref="UdsResponseEcho.Correlate"/> found a positive response to be.</summary>
internal enum UdsEchoMatch
{
    /// <summary>The request's own: its positive response SID, and the request bytes its
    /// service echoes repeated.</summary>
    Mine,

    /// <summary>Another request's: another service's positive response SID, or this
    /// service's with another sub-function, DID, routine, block or identifier.</summary>
    Stray,

    /// <summary>This service's positive response SID, but too short to carry the echo. Not a
    /// stray as far as the echo can tell: the physical client returns it and the service's own
    /// length check reports it as malformed rather than letting it vanish; the functional
    /// client drops it, as it always has.</summary>
    Truncated,
}

/// <summary>
/// Tells a positive response for a request apart from one for another request of the same
/// service, by the request bytes the response repeats (ISO 14229-1, the positive response
/// table of each service). Shared by the physical client's receive loop and the functional
/// client's collection: a late answer to an earlier request for another sub-function, DID,
/// routine or block is a stray to both (#340; Codex on #150).
/// </summary>
internal static class UdsResponseEcho
{
    private const byte PositiveResponseOffset = 0x40;
    private const byte SuppressPositiveResponseBit = 0x80;
    private const byte ReadDataByIdentifierSid = 0x22;
    private const byte ReadDataByPeriodicIdentifierSid = 0x2A;

    /// <summary>
    /// Correlates <paramref name="response"/> with <paramref name="request"/>: the request's
    /// positive response SID first, then the leading parameter bytes its service echoes
    /// (<see cref="EchoedRequestBytes"/>), with the suppressPosRspMsgIndication bit cleared
    /// from a sub-function. For ReadDataByIdentifier the first DID of the response is any
    /// requested one -- a server leaves unsupported DIDs out, in no particular order (ISO
    /// 14229-1 §10.2, #252); for ReadDataByPeriodicIdentifier, which echoes nothing, the
    /// periodic identifier the response carries data for is any requested one.
    /// </summary>
    public static UdsEchoMatch Correlate(ReadOnlySpan<byte> request, byte[] response)
    {
        if (request.Length == 0 || response.Length == 0) return UdsEchoMatch.Stray;
        byte sid = request[0];
        if (response[0] != (byte)(sid + PositiveResponseOffset)) return UdsEchoMatch.Stray;

        if (sid == ReadDataByIdentifierSid)
        {
            if (response.Length < 3) return UdsEchoMatch.Truncated;
            return NamesARequestedDataIdentifier(request, response) ? UdsEchoMatch.Mine : UdsEchoMatch.Stray;
        }

        if (sid == ReadDataByPeriodicIdentifierSid)
        {
            if (response.Length < 2) return UdsEchoMatch.Truncated;
            return NamesARequestedPeriodicIdentifier(request, response) ? UdsEchoMatch.Mine : UdsEchoMatch.Stray;
        }

        int echoed = Math.Min(EchoedRequestBytes(request), request.Length - 1);
        if (response.Length < 1 + echoed) return UdsEchoMatch.Truncated;
        for (int i = 0; i < echoed; i++)
        {
            byte expected = request[1 + i];
            if (i == 0 && HasSubFunction(sid)) expected &= unchecked((byte)~SuppressPositiveResponseBit);
            if (response[1 + i] != expected) return UdsEchoMatch.Stray;
        }
        return UdsEchoMatch.Mine;
    }

    /// <summary>
    /// How many request bytes after the SID a positive response repeats, for the services
    /// whose response layout starts with them (ISO 14229-1, the response tables of each
    /// service): the sub-function where there is one, then a DID (0x22 the first, 0x24, 0x2E,
    /// 0x2F -- Codex on #150), the sub-function and DID (0x2C), the routine identifier (0x31),
    /// the block sequence counter (0x36), the modeOfOperation of RequestFileTransfer (0x38 --
    /// Codex on #150), and for WriteMemoryByAddress (0x3D) the addressAndLengthFormatIdentifier
    /// with the address and size it sizes -- its low nibble the address's bytes, its high
    /// nibble the size's (Codex on #150). The rest echo nothing -- 0x14, 0x23, 0x34, 0x35,
    /// 0x37, 0x84 -- and are correlated on the positive SID alone; refusing them would refuse
    /// ClearDiagnosticInformation to everyone, the functional request there is (Codex on #150).
    /// </summary>
    public static int EchoedRequestBytes(ReadOnlySpan<byte> request)
    {
        byte sid = request[0];
        return sid switch
        {
            0x22 or 0x24 or 0x2E or 0x2F => 2,
            0x2C or 0x31 => 3,
            0x36 or 0x38 => 1,
            0x3D => request.Length < 2 ? 0 : 1 + (request[1] & 0x0F) + (request[1] >> 4),
            _ => HasSubFunction(sid) ? 1 : 0,
        };
    }

    /// <summary>
    /// The services whose second byte is a sub-function parameter, and so carry the
    /// suppressPosRspMsgIndication bit (ISO 14229-1 table 2, "sub-function" column). Not 0x2A:
    /// its transmissionMode is a plain parameter, and its response echoes nothing (Codex on
    /// #150).
    /// </summary>
    public static bool HasSubFunction(byte sid) => sid switch
    {
        0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x29 or 0x2C or 0x31 or 0x3E
            or 0x83 or 0x85 or 0x86 or 0x87 => true,
        _ => false,
    };

    // 0x22's positive response starts with a DID and its record; the DID must be one the
    // request asked for (the request's bytes after the SID, in pairs).
    private static bool NamesARequestedDataIdentifier(ReadOnlySpan<byte> request, byte[] response)
    {
        for (int i = 1; i + 1 < request.Length; i += 2)
        {
            if (request[i] == response[1] && request[i + 1] == response[2]) return true;
        }
        return false;
    }

    // 0x2A echoes nothing: its positive response starts with the periodic identifier it carries
    // data for, which must be one the request asked for (bytes after the transmission mode).
    private static bool NamesARequestedPeriodicIdentifier(ReadOnlySpan<byte> request, byte[] response)
    {
        for (int i = 2; i < request.Length; i++)
        {
            if (request[i] == response[1]) return true;
        }
        return false;
    }
}
