using System;
using CanKit.Core.Exceptions;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// A confirmed LSS service produced no slave response before the master's timeout (CiA 305
/// annex A.2). A timeout is not a confirmation: the service may still complete later, and a
/// following Fastscan step can mis-read that late response when
/// <see cref="LssMasterOptions.FastscanTimeout"/> is shorter than the slave's response time.
/// </summary>
public sealed class LssTimeoutException : CanKitException
{
    /// <summary>Constructs the exception for <paramref name="commandSpecifier"/>.</summary>
    public LssTimeoutException(byte commandSpecifier, TimeSpan timeout)
        : base(CanKitErrorCode.TransportOperationFailed,
            $"LSS command 0x{commandSpecifier:X2} was not confirmed within {timeout.TotalMilliseconds:0} ms.")
    {
        CommandSpecifier = commandSpecifier;
        Timeout = timeout;
    }

    /// <summary>The command specifier that was not confirmed.</summary>
    public byte CommandSpecifier { get; }

    /// <summary>How long the master waited.</summary>
    public TimeSpan Timeout { get; }
}
