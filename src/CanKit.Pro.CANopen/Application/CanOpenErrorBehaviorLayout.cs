using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Application;

/// <summary>
/// The error behaviour record <c>1029h</c> a profile defines (CiA 301 v4.2.0 §7.5.2.32): the
/// default of the communication error class at sub-index 01h, and the profile's further error
/// classes from sub-index 02h on, which CiA 301 leaves to the profile ("profile- or
/// manufacturer-specific error"). Asked for with <see cref="CanOpenApplicationRequirements.ErrorBehavior"/>.
/// </summary>
/// <remarks>
/// <para>
/// The node builds the record from it: sub-index 00h, constant, is the highest sub-index,
/// <c>1 +</c> the number of additional classes; sub-index 01h is the communication error, which the
/// node itself acts on as before; sub-index 02h on are the additional classes in list order. Every
/// class is UNSIGNED8 and read-write, and takes the values of Table 63: <c>00h</c> Pre-operational
/// (only from Operational), <c>01h</c> no change, <c>02h</c> Stopped. The node refuses any other
/// value, over SDO with <c>0609 0030h</c>, as it does for its own record; CiA 301 reserves
/// <c>03h</c> to <c>7Fh</c> and leaves <c>80h</c> to <c>FFh</c> to the manufacturer, which the node
/// does not implement.
/// </para>
/// <para>
/// The CiA 408 v2.2.0 record (§8.6.8, Table 56), for one: communication error and internal device
/// error, both defaulting to <c>01h</c> — <c>CommunicationErrorDefault = 1</c>,
/// <c>AdditionalDefaults = [1]</c>.
/// </para>
/// </remarks>
internal sealed class CanOpenErrorBehaviorLayout
{
    /// <summary>The power-on value of sub-index 01h, the communication error class: <c>00h</c> to
    /// <c>02h</c>. CiA 301 v4.2.0 §7.5.2.32 gives <c>00h</c>, which a profile may change.</summary>
    public byte CommunicationErrorDefault { get; init; }

    /// <summary>The power-on values of sub-index 02h on, one per additional error class, each
    /// <c>00h</c> to <c>02h</c>; at most 253 of them (sub-indices 02h to FEh). Empty for none.</summary>
    public IReadOnlyList<byte> AdditionalDefaults { get; init; } = Array.Empty<byte>();
}
