using System;

namespace CanKit.Pro.Uds;

/// <summary>
/// One ECU's answer to a functionally addressed request (see <see cref="UdsFunctionalClient"/>):
/// where it came from, and the response bytes read as UDS.
/// </summary>
public sealed class UdsFunctionalResponse
{
    /// <summary>Creates a response; the client is the intended caller.</summary>
    public UdsFunctionalResponse(uint sourceCanId, byte[] response)
    {
        SourceCanId = sourceCanId;
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    /// <summary>The CAN identifier the ECU answered on.</summary>
    public uint SourceCanId { get; }

    /// <summary>The response bytes, starting with the response SID (or 0x7F).</summary>
    public byte[] Response { get; }

    /// <summary>Whether this is a negative response (SID 0x7F).</summary>
    public bool IsNegative => Response.Length >= 1 && Response[0] == 0x7F;

    /// <summary>The negative response code, or <c>null</c> for a positive response.</summary>
    public byte? NegativeResponseCode
        => IsNegative && Response.Length >= 3 ? Response[2] : null;
}
