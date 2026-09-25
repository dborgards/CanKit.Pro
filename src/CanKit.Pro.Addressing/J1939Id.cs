using System;

namespace CanKit.Pro.Addressing
{
    /// <summary>
    /// Composes and decomposes 29-bit J1939 (SAE J1939-21) CAN identifiers (arc42 "Adressierungs-
    /// Helfer"; SRS FR-RAW-040). This generalizes the single hard-coded-PGN 29-bit ID builder
    /// previously scattered inside <c>IsoTpEndpoint.CreateNormalFixed</c> into a reusable,
    /// general-purpose PGN/Priority/Source-Address helper any protocol layer can call directly.
    /// </summary>
    public static class J1939Id
    {
        /// <summary>
        /// Decomposes a 29-bit CAN ID into its J1939 fields (Priority, Reserved, DataPage,
        /// PDU-Format, PDU-Specific, Source-Address, and the derived PGN/destination-address).
        /// </summary>
        /// <param name="canId">
        /// The 29-bit extended CAN ID (flag bits, if any, must already be stripped -- pass
        /// <c>CanFrame.ID</c>/<c>CanFrameView.ID</c> as-is, they are already flag-stripped).
        /// </param>
        /// <remarks>
        /// The value alone cannot tell an 11-bit identifier from a 29-bit one -- every 11-bit
        /// value is also a valid 29-bit one, with priority 0 and PDU Format 0 -- so an 11-bit
        /// identifier passed here decomposes into fields it never had. The caller knows the
        /// frame's kind: pass it to <see cref="Decompose(uint, bool)"/>, or skip frames that are
        /// not extended before calling this (#55).
        /// </remarks>
        public static J1939Fields Decompose(uint canId)
        {
            CanIdRange.ValidateExtended(canId);
            var priority = (byte)((canId >> 26) & 0x7);
            var reserved = ((canId >> 25) & 0x1) != 0;
            var dataPage = (byte)((canId >> 24) & 0x1);
            var pduFormat = (byte)((canId >> 16) & 0xFF);
            var pduSpecific = (byte)((canId >> 8) & 0xFF);
            var sourceAddress = (byte)(canId & 0xFF);
            return new J1939Fields(priority, reserved, dataPage, pduFormat, pduSpecific, sourceAddress);
        }

        /// <summary>
        /// As <see cref="Decompose(uint)"/>, for a caller that knows the frame's kind: an
        /// identifier from a frame that is not extended is refused, since J1939 uses 29-bit
        /// identifiers only and an 11-bit one would decompose into fields it never had (#55).
        /// </summary>
        /// <param name="canId">The CAN ID, flag bits stripped.</param>
        /// <param name="isExtendedFrame">
        /// Whether the frame carrying it is an extended (29-bit) frame -- <c>CanFrame.IsExtendedFrame</c>.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="isExtendedFrame"/> is <c>false</c>.</exception>
        public static J1939Fields Decompose(uint canId, bool isExtendedFrame)
        {
            if (!isExtendedFrame)
                throw new ArgumentException("J1939 identifiers are 29-bit: an 11-bit frame's identifier has no J1939 fields.", nameof(isExtendedFrame));
            return Decompose(canId);
        }

        /// <summary>
        /// Composes a 29-bit CAN ID from its raw J1939 fields.
        /// </summary>
        /// <param name="priority">Message priority, 0 (highest) – 7 (lowest). Only the low 3 bits are used.</param>
        /// <param name="reserved">Reserved bit (bit 25); pass false unless a specific application defines otherwise.</param>
        /// <param name="dataPage">Data Page bit (bit 24); only the low bit is used.</param>
        /// <param name="pduFormat">PDU Format (PF), bits 23-16.</param>
        /// <param name="pduSpecific">PDU Specific (PS), bits 15-8: destination address (PF &lt; 240) or Group Extension (PF &gt;= 240).</param>
        /// <param name="sourceAddress">Source Address (SA), bits 7-0.</param>
        public static uint Compose(byte priority, bool reserved, byte dataPage, byte pduFormat, byte pduSpecific, byte sourceAddress)
        {
            if (priority > 7) throw new ArgumentOutOfRangeException(nameof(priority), priority, "Priority must be in [0, 7].");
            if (dataPage > 1) throw new ArgumentOutOfRangeException(nameof(dataPage), dataPage, "DataPage must be 0 or 1.");

            var id = ((uint)priority << 26)
                     | ((reserved ? 1u : 0u) << 25)
                     | ((uint)dataPage << 24)
                     | ((uint)pduFormat << 16)
                     | ((uint)pduSpecific << 8)
                     | sourceAddress;
            return CanIdRange.ValidateExtended(id);
        }

        /// <summary>
        /// Composes a 29-bit CAN ID from a PGN, the way protocol code usually thinks about it: "I
        /// want to send this PGN, at this priority, from this source, to this destination."
        /// </summary>
        /// <param name="priority">Message priority, 0 (highest) – 7 (lowest).</param>
        /// <param name="pgn">
        /// Parameter Group Number, as returned by <see cref="J1939Fields.Pgn"/> (up to 18 bits:
        /// Reserved&lt;&lt;17 | DataPage&lt;&lt;16 | PduFormat&lt;&lt;8 | GroupExtension).
        /// </param>
        /// <param name="sourceAddress">Source Address (SA), bits 7-0.</param>
        /// <param name="destinationAddress">
        /// Destination address for a PDU1 (peer-to-peer) PGN -- i.e. when the PGN's PDU Format
        /// byte is &lt; 240. Ignored for a PDU2 (broadcast-only) PGN, since PDU2 messages have no
        /// destination address (defaults to the conventional global/broadcast address 0xFF, which
        /// is simply unused in that case).
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="pgn"/> does not fit in 18 bits, or is a PDU1 PGN whose low byte is
        /// not zero -- SAE J1939-21 defines a PDU1 PGN with its PDU Specific byte as 0, so such
        /// a value is not a PGN, and the byte is not silently discarded (#55).
        /// </exception>
        public static uint ComposePgn(byte priority, uint pgn, byte sourceAddress, byte destinationAddress = 0xFF)
        {
            if (pgn > 0x3FFFF) throw new ArgumentOutOfRangeException(nameof(pgn), pgn, "PGN must fit in 18 bits (Reserved|DataPage|PF|GE).");

            var reserved = ((pgn >> 17) & 0x1) != 0;
            var dataPage = (byte)((pgn >> 16) & 0x1);
            var pduFormat = (byte)((pgn >> 8) & 0xFF);
            if (pduFormat < 240 && (pgn & 0xFF) != 0)
                throw new ArgumentOutOfRangeException(nameof(pgn), pgn,
                    "A PDU1 PGN (PDU Format < 240) has a PDU Specific byte of 0; the destination address is a separate argument.");
            var pduSpecific = pduFormat < 240 ? destinationAddress : (byte)(pgn & 0xFF);
            return Compose(priority, reserved, dataPage, pduFormat, pduSpecific, sourceAddress);
        }
    }
}
