using System;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939Tp;

namespace CanKit.Pro.J1939;

/// <summary>
/// Immutable configuration for <see cref="IJ1939Node"/>. Captures the node's identity
/// (<see cref="Name"/>), the SAE J1939-81 arbitration window <see cref="ClaimAnnounceTimeout"/>,
/// the claim and receive priorities, and the transport-protocol options passed through to the shared
/// <see cref="IJ1939TpChannel"/> for &gt;8-byte payloads.
/// </summary>
public sealed class J1939NodeOptions
{
    /// <summary>Constructs options with the given 64-bit NAME. All other fields default.</summary>
    /// <param name="name">The node's SAE J1939-81 NAME used for address-claim arbitration.</param>
    public J1939NodeOptions(J1939Name name)
    {
        Name = name;
    }

    /// <summary>The node's SAE J1939-81 NAME (used for address claim arbitration).</summary>
    public J1939Name Name { get; }

    /// <summary>
    /// Duration <see cref="IJ1939Node.ClaimAddressAsync"/> waits after transmitting the initial
    /// Address Claim (PGN 0xEE00) before declaring the address claimed. SAE J1939-81 §4.4.3.3
    /// mandates 250 ms — that is the default. Shorten for tests only.
    /// </summary>
    public TimeSpan ClaimAnnounceTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Arbitrary-address fallback after a lost claim contest (SRS FR-J1939-004 / SAE J1939-81
    /// §4.5): when the node loses a contest for the preferred address it retries with the next
    /// candidate from the arbitrary address field (0x80..0xF7, wrapping once) and only
    /// broadcasts Cannot Claim (SA=0xFE) when the whole field is exhausted. <c>null</c>
    /// (default) derives the behavior from the NAME's Arbitrary Address Capable bit (J1939-81
    /// §4.5.1); set explicitly to override the NAME bit.
    /// </summary>
    public bool? EnableArbitraryAddressClaiming { get; init; }

    /// <summary>
    /// Default priority reported for inbound reassembled J1939-TP payloads, where the transport
    /// channel does not currently expose the original TP.CM / TP.DT CAN priority. Defaults to 6.
    /// </summary>
    public byte DefaultPriority { get; init; } = 6;

    /// <summary>
    /// Priority used by this node for Address Claim / Cannot Claim (PGN 0xEE00) and Request-PGN
    /// (PGN 0xEA00) frames. Defaults to 6.
    /// </summary>
    public byte ClaimPriority { get; init; } = 6;

    /// <summary>
    /// Not used. It sized an internal receive buffer that no public member reads; the node
    /// delivers every received message through <see cref="IJ1939Node.MessageReceived"/> and no
    /// longer keeps a copy (#355). Still validated (it must be at least 1, default 128), so a
    /// configuration that was refused is refused as before. Removed in 2.0 (#299).
    /// </summary>
    [Obsolete("Not used: the node keeps no receive buffer; receive through IJ1939Node.MessageReceived. Removed in 2.0 (#299).")]
    public int ReceiveBufferCapacity { get; init; } = 128;

    /// <summary>
    /// Options for the <see cref="IJ1939TpChannel"/> the node opens for multi-frame payloads
    /// (SRS FR-J1939-006). Defaults to a fresh <see cref="J1939TpOptions"/>. The node always
    /// opens that channel itself; no factory overload takes a pre-built one (#58).
    /// </summary>
    public J1939TpOptions TransportOptions { get; init; } = new J1939TpOptions();

    /// <summary>Validates invariants. Called from the node's constructor, which both
    /// <see cref="J1939Node"/>.<c>Open</c> overloads reach.</summary>
    internal void Validate()
    {
        if (ClaimAnnounceTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ClaimAnnounceTimeout), ClaimAnnounceTimeout,
                "ClaimAnnounceTimeout must be positive.");
        if (DefaultPriority > 7)
            throw new ArgumentOutOfRangeException(nameof(DefaultPriority), DefaultPriority,
                "J1939 priority must be in [0, 7].");
        if (ClaimPriority > 7)
            throw new ArgumentOutOfRangeException(nameof(ClaimPriority), ClaimPriority,
                "J1939 priority must be in [0, 7].");
#pragma warning disable CS0618 // validated until 2.0 removes it, so nothing that was refused is accepted now
        if (ReceiveBufferCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferCapacity), ReceiveBufferCapacity,
                "ReceiveBufferCapacity must be >= 1.");
#pragma warning restore CS0618
        if (TransportOptions is null)
            throw new ArgumentNullException(nameof(TransportOptions));
    }
}
