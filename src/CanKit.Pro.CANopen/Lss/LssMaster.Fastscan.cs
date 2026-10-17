using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Lss;

internal sealed partial class LssMaster
{
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
            var scanned = await ScanFieldAsync(fields[index], (byte)index, cancellationToken).ConfigureAwait(false);
            if (scanned is not uint id)
                return LssFastscanResult.NoMatch(AddressFrom(found));
            found[index] = id;

            int next = index + 1;
            while (next < 4 && fields[next].Mode == LssFastscanFieldMode.Skip) next++;
            // The last confirmation uses LSSNext 0, which is what switches the slave that matched
            // the whole address into configuration (§6.7.3).
            byte lssNext = next >= 4 ? (byte)0 : (byte)next;
            if (!await ProbeAsync(id, 0, (byte)index, lssNext, cancellationToken).ConfigureAwait(false))
                return LssFastscanResult.NoMatch(AddressFrom(found));
            if (lssNext == 0)
                return LssFastscanResult.Found(AddressFrom(found));
            index = next;
        }
    }

    private async Task<uint?> ScanFieldAsync(LssFastscanField field, byte lssSub, CancellationToken cancellationToken)
    {
        if (field.Mode == LssFastscanFieldMode.Match || field.KnownHighBitCount == 32)
            return field.Value;

        uint id = 0;
        int firstUnknownBit = 31;
        if (field.KnownHighBitCount > 0)
        {
            int unknown = 32 - field.KnownHighBitCount;
            uint prefixMask = unknown == 0 ? 0xFFFFFFFFu : 0xFFFFFFFFu << unknown;
            id = field.Value & prefixMask;
            // One step checks the known prefix (annex A.3.4). A miss means no slave has it.
            if (!await ProbeAsync(id, (byte)unknown, lssSub, lssSub, cancellationToken).ConfigureAwait(false))
                return null;
            firstUnknownBit = unknown - 1;
        }

        // Bits are proposed as 0. A response means at least one still-matching slave has 0 there,
        // and 0 wins (§6.7.2). No response means every such slave has 1, so the bit is set.
        // The mask of the next step includes every bit already decided, so a slave that missed an
        // earlier bit cannot answer a later one. Slaves are not dropped on a single miss: the
        // "no response, set the bit" step has to reach the same slaves.
        for (int bit = firstUnknownBit; bit >= 0; bit--)
        {
            if (!await ProbeAsync(id, (byte)bit, lssSub, lssSub, cancellationToken).ConfigureAwait(false))
                id |= 1u << bit;
        }
        return id;
    }

    private async Task<bool> ProbeAsync(uint idNumber, byte bitCheck, byte lssSub, byte lssNext,
        CancellationToken cancellationToken)
    {
        int watermark = Watermark();
        await SendAsync(LssFrames.Fastscan(idNumber, bitCheck, lssSub, lssNext), cancellationToken)
            .ConfigureAwait(false);
        var response = await ReceiveAsync(watermark, static d => d[0] == LssCommand.IdentifySlave,
            Options.FastscanTimeout, cancellationToken).ConfigureAwait(false);
        if (Options.FastscanCycleTime > TimeSpan.Zero)
            await Task.Delay(Options.FastscanCycleTime, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    private static LssAddress AddressFrom(uint[] found)
        => new(found[0], found[1], found[2], found[3]);
}
