namespace CanKit.Pro.Addressing
{
    /// <summary>
    /// The decomposed fields of a 29-bit J1939 CAN identifier (SAE J1939-21) — bit layout
    /// (MSB→LSB): Priority(3) | Reserved(1) | DataPage(1) | PDU-Format(8) | PDU-Specific(8) |
    /// Source-Address(8).
    /// </summary>
    public readonly struct J1939Fields
    {
        internal J1939Fields(byte priority, bool reserved, byte dataPage, byte pduFormat, byte pduSpecific, byte sourceAddress)
        {
            Priority = priority;
            Reserved = reserved;
            DataPage = dataPage;
            PduFormat = pduFormat;
            PduSpecific = pduSpecific;
            SourceAddress = sourceAddress;
        }

        /// <summary>Message priority, 0 (highest) – 7 (lowest).</summary>
        public byte Priority { get; }

        /// <summary>Reserved bit (bit 25); conventionally 0.</summary>
        public bool Reserved { get; }

        /// <summary>Data Page bit (bit 24).</summary>
        public byte DataPage { get; }

        /// <summary>PDU Format (PF), bits 23-16.</summary>
        public byte PduFormat { get; }

        /// <summary>
        /// PDU Specific (PS), bits 15-8: a destination address when <see cref="IsPdu1"/> is true
        /// (PF &lt; 240), or a Group Extension (part of the PGN) otherwise.
        /// </summary>
        public byte PduSpecific { get; }

        /// <summary>Source Address (SA), bits 7-0.</summary>
        public byte SourceAddress { get; }

        /// <summary>
        /// True for PDU1 (peer-to-peer, destination-addressable) format: <see cref="PduFormat"/>
        /// &lt; 240. False for PDU2 (broadcast-only).
        /// </summary>
        public bool IsPdu1 => PduFormat < 240;

        /// <summary>
        /// Destination address for PDU1 messages (equal to <see cref="PduSpecific"/>), or null for
        /// PDU2 messages (which have no destination — they are always broadcast).
        /// </summary>
        public byte? DestinationAddress => IsPdu1 ? PduSpecific : null;

        /// <summary>
        /// The Parameter Group Number: <c>(Reserved&lt;&lt;17) | (DataPage&lt;&lt;16) | (PduFormat&lt;&lt;8) |
        /// GroupExtension</c>, where the Group Extension is <see cref="PduSpecific"/> for PDU2
        /// messages and 0 for PDU1 messages (whose PS is a destination address, not part of the
        /// PGN).
        /// </summary>
        public uint Pgn =>
            ((Reserved ? 1u : 0u) << 17) | ((uint)DataPage << 16) | ((uint)PduFormat << 8) | (IsPdu1 ? 0u : PduSpecific);
    }
}
