using System;

namespace CanKit.Pro.Addressing
{
    /// <summary>
    /// Validated construction/checking for 11-bit (Standard) and 29-bit (Extended) CAN IDs
    /// (arc42 "Adressierungs-Helfer"; SRS FR-RAW-040).
    /// </summary>
    /// <remarks>
    /// Every layer that already masks an ID (<c>CanFrame</c>/<c>CanFrameView</c>,
    /// <c>IsoTpEndpoint</c>) does so silently (<c>id &amp; mask</c>) rather than rejecting an
    /// out-of-range value — appropriate on a hot path, but it means a caller-supplied ID that is
    /// too wide is truncated without any signal. These helpers are the opposite: an explicit,
    /// throwing check for call sites (configuration, protocol-instance setup) where silently
    /// accepting a truncated ID would be the wrong failure mode.
    /// </remarks>
    public static class CanIdRange
    {
        /// <summary>Largest valid 11-bit standard CAN ID (inclusive).</summary>
        public const uint StandardMax = 0x7FF;

        /// <summary>Largest valid 29-bit extended CAN ID (inclusive).</summary>
        public const uint ExtendedMax = 0x1FFFFFFF;

        /// <summary>True when <paramref name="id"/> fits in 11 bits.</summary>
        public static bool IsValidStandard(uint id) => id <= StandardMax;

        /// <summary>True when <paramref name="id"/> fits in 29 bits.</summary>
        public static bool IsValidExtended(uint id) => id <= ExtendedMax;

        /// <summary>
        /// Returns <paramref name="id"/> unchanged if it fits in 11 bits, otherwise throws.
        /// </summary>
        public static uint ValidateStandard(uint id)
        {
            if (!IsValidStandard(id))
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Standard (11-bit) CAN ID must be in [0, 0x{StandardMax:X}].");
            return id;
        }

        /// <summary>
        /// Returns <paramref name="id"/> unchanged if it fits in 29 bits, otherwise throws.
        /// </summary>
        public static uint ValidateExtended(uint id)
        {
            if (!IsValidExtended(id))
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Extended (29-bit) CAN ID must be in [0, 0x{ExtendedMax:X}].");
            return id;
        }
    }
}
