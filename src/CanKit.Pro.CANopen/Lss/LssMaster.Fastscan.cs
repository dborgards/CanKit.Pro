using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;

namespace CanKit.Pro.CANopen.Lss;

internal sealed partial class LssMaster
{
    private long _nextFastscanAt;

    /// <inheritdoc />
    public Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new LssFastscanRequest();
        var fields = new[]
        {
            request.VendorId, request.ProductCode, request.RevisionNumber, request.SerialNumber,
        };
        ValidateFastscan(fields);
        return Execute(ct => RunFastscanAsync(fields, ct), cancellationToken);
    }

    private static void ValidateFastscan(LssFastscanField[] fields)
    {
        int skips = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field is null)
                throw new ArgumentException("Each LSS Fastscan field must be set.");
            if (i == 0 && field.Mode == LssFastscanFieldMode.Skip)
                throw new ArgumentException("The vendor-id cannot be skipped in LSS Fastscan.");
            if (field.Mode == LssFastscanFieldMode.Skip) skips++;
            if (field.KnownHighBitCount is < 0 or > 32)
                throw new ArgumentOutOfRangeException(nameof(field.KnownHighBitCount), field.KnownHighBitCount,
                    "Known high bits are 0..32.");
            if (field.Mode != LssFastscanFieldMode.Scan && field.KnownHighBitCount != 0)
                throw new ArgumentException("KnownHighBitCount applies only when the field is scanned.");
        }
        if (skips > 2)
            throw new ArgumentException("LSS Fastscan can skip at most two LSS numbers.");
    }

    private async Task<LssFastscanResult> RunFastscanAsync(LssFastscanField[] fields, CancellationToken cancellationToken)
    {
        // BitCheck 80h: every unconfigured slave responds and forgets a previous scan (§6.7.2).
        if (!await ProbeAsync(0, LssCommand.FastscanReset, 0, 0, cancellationToken).ConfigureAwait(false))
            return LssFastscanResult.NoUnconfiguredSlave();

        var found = new uint[4];
        int index = 0;
        while (true)
        {
            // Validation refuses to skip the vendor-id and refuses to skip more than two numbers,
            // so index always lands on a number that is scanned and the loop returns on LSSNext 0.
            int next = index + 1;
            while (next < 4 && fields[next].Mode == LssFastscanFieldMode.Skip) next++;
            // The last confirmation uses LSSNext 0, which is what switches the slave that matched
            // the whole address into configuration (§6.7.3, annex A.3.5). Bit 0 of this field is
            // sent with that same LSSNext. For every earlier field that value is the next field,
            // not 0, so a slave that treats BitCheck 0 and LSSNext 0 as the switch stays in the scan.
            byte lssNext = next >= 4 ? (byte)0 : (byte)next;
            var scanned = await ScanFieldAsync(fields[index], (byte)index, lssNext, cancellationToken)
                .ConfigureAwait(false);
            if (!scanned.Matched)
                return LssFastscanResult.NoMatch(AddressFrom(found));
            // Bit 0 already carried this field's confirmation LSSNext (annex A.3.5). A slave
            // that answered has stored that LSSNext as its position, or has switched into
            // configuration when the value was 0, and will not answer the same frame again.
            // A slave whose least significant bit is 1 did not answer, so it is still on this
            // field and the complete number is sent once more (§6.7.3).
            if (!scanned.Bit0Acknowledged
                && !await ProbeAsync(scanned.Id, 0, (byte)index, lssNext, cancellationToken).ConfigureAwait(false))
                return LssFastscanResult.NoMatch(AddressFrom(found));
            found[index] = scanned.Id;
            if (lssNext == 0)
                return LssFastscanResult.Found(AddressFrom(found));
            index = next;
        }
    }

    private async Task<FieldScan> ScanFieldAsync(LssFastscanField field, byte lssSub, byte lssNextAtBit0,
        CancellationToken cancellationToken)
    {
        if (field.Mode == LssFastscanFieldMode.Match || field.KnownHighBitCount == 32)
            return FieldScan.Hit(field.Value);

        uint id = 0;
        int firstUnknownBit = 31;
        if (field.KnownHighBitCount > 0)
        {
            // KnownHighBitCount 32 already returned above, so at least one bit is still unknown.
            int unknown = 32 - field.KnownHighBitCount;
            uint prefixMask = 0xFFFFFFFFu << unknown;
            id = field.Value & prefixMask;
            // One step checks the known prefix (annex A.3.4). A miss means no slave has it.
            if (!await ProbeAsync(id, (byte)unknown, lssSub, lssSub, cancellationToken).ConfigureAwait(false))
                return FieldScan.Miss();
            firstUnknownBit = unknown - 1;
        }

        // Bits are proposed as 0. A response means at least one still-matching slave has 0 there,
        // and 0 wins (§6.7.2). No response means every such slave has 1, so the bit is set.
        // The mask of the next step includes every bit already decided, so a slave that missed an
        // earlier bit cannot answer a later one. Slaves are not dropped on a single miss: the
        // "no response, set the bit" step has to reach the same slaves.
        // Bit 0 is sent with the LSSNext of the following confirmation (annex A.3.5). For the
        // vendor-id that value is the next field, never 0: BitCheck 0 together with LSSNext 0 is
        // the completion that switches the slave, and sending it while the vendor-id is still
        // being decided would leave the scan with nobody answering the later fields. An
        // acknowledgement of bit 0 means the slave has already taken that LSSNext.
        bool bit0Acknowledged = false;
        for (int bit = firstUnknownBit; bit >= 0; bit--)
        {
            byte lssNext = bit == 0 ? lssNextAtBit0 : lssSub;
            if (!await ProbeAsync(id, (byte)bit, lssSub, lssNext, cancellationToken).ConfigureAwait(false))
                id |= 1u << bit;
            else if (bit == 0)
                bit0Acknowledged = true;
        }
        return FieldScan.Hit(id, bit0Acknowledged);
    }

    private readonly struct FieldScan
    {
        private FieldScan(bool matched, uint id, bool bit0Acknowledged)
        {
            Matched = matched;
            Id = id;
            Bit0Acknowledged = bit0Acknowledged;
        }

        public bool Matched { get; }
        public uint Id { get; }
        public bool Bit0Acknowledged { get; }

        public static FieldScan Hit(uint id, bool bit0Acknowledged = false) => new(true, id, bit0Acknowledged);
        public static FieldScan Miss() => new(false, 0, false);
    }

    private async Task<bool> ProbeAsync(uint idNumber, byte bitCheck, byte lssSub, byte lssNext,
        CancellationToken cancellationToken)
    {
        // Only consecutive Fastscan probes need this gap. A completed scan releases the gate
        // immediately; another scan still observes the previous probe's gap.
        await DelayUntilAsync(_nextFastscanAt, cancellationToken).ConfigureAwait(false);
        long responseUntil = 0;
        try
        {
            var sent = await SendForReplyAsync(LssFrames.Fastscan(idNumber, bitCheck, lssSub, lssNext),
                Options.FastscanTimeout, cancellationToken).ConfigureAwait(false);
            long start = sent.Transmit > 0 ? sent.Transmit : _time.GetTimestamp();
            responseUntil = TickMath.DueAt(start, Options.FastscanTimeout, _time.Frequency);
            // Annex A.3.3: the master cycle contains the whole response timeout. A second
            // slave's 4Fh must not become the next probe's answer, even after cancellation.
            var response = await ReceiveAsync(sent, static d => d.Span[0] == LssCommand.IdentifySlave,
                Options.FastscanTimeout, cancellationToken, drain: true).ConfigureAwait(false);
            return response is not null;
        }
        finally
        {
            long end = Math.Max(_time.GetTimestamp(), Math.Max(responseUntil, _nextCommandAt));
            _nextFastscanAt = TickMath.DueAt(end, Options.FastscanCycleTime, _time.Frequency);
        }
    }

    private static LssAddress AddressFrom(uint[] found)
        => new(found[0], found[1], found[2], found[3]);
}
