using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// Layer Setting Services (CiA 305) and the two objects that make a change outlive a power cycle.
/// Everything here but the first check needs the write or commission tier, because LSS puts a
/// slave into its configuration state — a device in LSS configuration state is not doing its job
/// — and because a stored node-id is how a bus gets rewired by accident.
/// </summary>
internal static class LssAndStoreChecks
{
    /// <summary>
    /// Puts every slave into LSS Waiting, the resting state. Waiting is the precondition of the
    /// selective switch and of Fastscan — both answer from no other state — so each LSS check
    /// normalises rather than inheriting whatever the previous one left behind.
    /// </summary>
    private static Task NormaliseToWaitingAsync(Session session, CancellationToken cancellationToken)
        => session.Lss.SwitchStateGlobalAsync(LssState.Waiting, cancellationToken);

    /// <summary>
    /// Whether the bound description says outright that the device has no LSS. CiA 305 support is
    /// optional and an EDS declares it with LSS_Supported, so a file that says no turns the whole
    /// group into "not applicable" instead of three services timing out.
    /// </summary>
    private static bool DeclaredWithoutLss(Session session)
        => session.TargetDescription is { } description && !description.DeviceInfo.LssSupported;

    private static string NoLssReason(Session session)
        => $"{System.IO.Path.GetFileName(session.Options.EdsPath)} declares LSS_Supported=0: the device has no Layer "
           + "Setting Services, which CiA 301 permits.";

    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        await book.RunAsync("LSS-01", "lss", "Unconfigured slaves on the bus", Tier.Write, "FR-CO-047", async () =>
        {
            if (DeclaredWithoutLss(session)) return Outcome.NotApplicable(NoLssReason(session));

            // This is the one LSS service that is safe on a working bus: it addresses only slaves
            // whose node-id is FFh, i.e. devices that are not participating yet. It still needs
            // the write tier, because the global state command it sends reaches every slave.
            //
            // Waiting, not Configuration. The identify request needs no particular state — the
            // node answers IdentifyNonConfiguredRemote whatever its LSS state — but the selective
            // switch (LSS-02) and Fastscan (LSS-03) both answer only from Waiting, so a check that
            // left the bus in Configuration would quietly disable the two after it. Normalising to
            // Waiting also makes each LSS check independent of the order they run in.
            await NormaliseToWaitingAsync(session, cancellationToken).ConfigureAwait(false);
            var any = await session.Lss.IdentifyNonConfiguredSlavesAsync(cancellationToken).ConfigureAwait(false);
            var measurements = new Dictionary<string, string> { ["unconfigured slave present"] = any.ToString() };
            return Outcome.Pass(any
                ? "At least one slave has no node-id yet and answered the identify request. LSS-03 can read its address."
                : "No unconfigured slave answered; every device on this bus already has a node-id.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("LSS-02", "lss", "Selective addressing by LSS address, and inquire", Tier.Write, "FR-CO-047", async () =>
        {
            if (DeclaredWithoutLss(session)) return Outcome.NotApplicable(NoLssReason(session));

            // The LSS address is 1018h:01..04. Reading it over SDO first and then asking LSS to
            // select the device by it is what makes this a conformance check rather than two
            // unrelated reads: the two paths have to agree about the same device.
            LssAddress expected;
            try
            {
                var vendor = await session.Client.SdoUploadAsync(session.Target, 0x1018, 0x01, cancellationToken).ConfigureAwait(false);
                var product = await session.Client.SdoUploadAsync(session.Target, 0x1018, 0x02, cancellationToken).ConfigureAwait(false);
                var revision = await session.Client.SdoUploadAsync(session.Target, 0x1018, 0x03, cancellationToken).ConfigureAwait(false);
                var serial = await session.Client.SdoUploadAsync(session.Target, 0x1018, 0x04, cancellationToken).ConfigureAwait(false);
                if (new[] { vendor, product, revision, serial }.Any(v => v.Length != 4))
                    return Outcome.NeedsSetup("1018h:01..04 did not all read back as UNSIGNED32; see IDN-03.");

                expected = new LssAddress(
                    BitConverter.ToUInt32(vendor, 0), BitConverter.ToUInt32(product, 0),
                    BitConverter.ToUInt32(revision, 0), BitConverter.ToUInt32(serial, 0));
            }
            catch (SdoAbortException abort)
            {
                return Outcome.NeedsSetup($"1018h could not be read over SDO ({IdentityAndSdoChecks.Describe(abort)}), so there is no address to select by.");
            }

            var measurements = new Dictionary<string, string> { ["LSS address from 1018h"] = expected.ToString() };
            try
            {
                // The slave answers a selective switch only from Waiting, so the state is
                // normalised rather than assumed from whatever ran before.
                await NormaliseToWaitingAsync(session, cancellationToken).ConfigureAwait(false);
                var selected = await session.Lss.SwitchStateSelectiveAsync(expected, cancellationToken).ConfigureAwait(false);
                measurements["selective switch answered"] = selected.ToString();
                if (!selected)
                {
                    // A device the file declares as having no LSS is not failing by staying
                    // silent; it is answering the only way it can. Without a declaration the
                    // silence is genuinely ambiguous — LSS is optional in CiA 301 — so it is
                    // inconclusive rather than a fault. An earlier version failed both. Found in
                    // review of #388.
                    if (session.TargetDescription is { } description && !description.DeviceInfo.LssSupported)
                    {
                        return Outcome.NotApplicable(
                            $"{System.IO.Path.GetFileName(session.Options.EdsPath)} declares LSS_Supported=0, and the "
                            + "device did not answer a selective switch. That is consistent: LSS is optional in "
                            + "CiA 301 and this device does not have it.");
                    }

                    return Outcome.Inconclusive(
                        "The device did not answer a selective LSS switch for the address its own 1018h reports. Either "
                        + "it does not implement LSS — which CiA 301 permits, and which an EDS would say with "
                        + "LSS_Supported — or its LSS address differs from its identity object, which CiA 305 forbids. "
                        + "Nothing observed here separates the two; pass an EDS and the check can.",
                        Attribution.Unknown, measurements);
                }

                var inquired = await session.Lss.InquireAddressAsync(cancellationToken).ConfigureAwait(false);
                var nodeId = await session.Lss.InquireNodeIdAsync(cancellationToken).ConfigureAwait(false);
                measurements["LSS address from inquire"] = inquired.ToString();
                measurements["node-id from inquire"] = nodeId.ToString(CultureInfo.InvariantCulture);

                var problems = new List<string>();
                if (inquired != expected) problems.Add($"inquire returned {inquired}, 1018h says {expected}");
                if (nodeId != session.Target) problems.Add($"inquire returned node-id {nodeId}, the device answers SDO on {session.Target}");

                return problems.Count == 0
                    ? Outcome.Pass("The device selected on its 1018h address and inquired the same address and node-id back.", measurements)
                    : Outcome.Fail(string.Join("; ", problems) + ".", measurements);
            }
            catch (LssTimeoutException timeout)
            {
                measurements["timeout on command specifier"] = $"0x{timeout.CommandSpecifier:X2} after {timeout.Timeout.TotalMilliseconds:F0} ms";
                return Outcome.Inconclusive(
                    "The device did not answer an LSS request. LSS is optional in CiA 301 — a device that does not "
                    + "implement it is silent on 7E5h, which is indistinguishable from one that is not listening.",
                    Attribution.Device, measurements);
            }
            finally
            {
                try
                {
                    await session.Lss.SwitchStateGlobalAsync(LssState.Waiting, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    book.Note("LSS-02 cleanup", ex);
                }
            }
        }).ConfigureAwait(false);

        await book.RunAsync("LSS-03", "lss", "Fastscan finds an unconfigured slave's address", Tier.Commission, "FR-CO-049", async () =>
        {
            if (DeclaredWithoutLss(session)) return Outcome.NotApplicable(NoLssReason(session));

            // Fastscan leaves the slave it finds selected and in configuration state, which is why
            // this is the commission tier even though it writes nothing: a device left there has
            // stopped doing its job until it is switched back or power-cycled.
            //
            // Waiting first, and this is the whole service's precondition rather than tidiness:
            // the slave answers Fastscan only from Waiting and only while unconfigured. An earlier
            // version switched to Configuration here, which made the reset probe go unanswered and
            // the check report "no unconfigured slave" -- a false negative that looked like a clean
            // bus. Found in review of #388.
            await NormaliseToWaitingAsync(session, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await session.Lss.FastscanAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var measurements = new Dictionary<string, string> { ["status"] = result.Status.ToString() };
                if (result.Status == LssFastscanStatus.Found) measurements["address"] = result.Address.ToString();
                else if (result.Status == LssFastscanStatus.NoMatch) measurements["partial"] = result.Address.ToString();

                return result.Status switch
                {
                    LssFastscanStatus.Found => Outcome.Pass($"Fastscan identified {result.Address}.", measurements),
                    LssFastscanStatus.NoUnconfiguredSlave => Outcome.NotApplicable(
                        "No unconfigured slave is on the bus, so Fastscan has nothing to find. Power up a device "
                        + "without a node-id to exercise this."),
                    _ => Outcome.Fail(
                        "Fastscan started and did not resolve an address, although a slave answered the first step. "
                        + "That is a slave which answers the protocol inconsistently.", measurements),
                };
            }
            catch (LssTimeoutException timeout)
            {
                return Outcome.Inconclusive(
                    $"Fastscan timed out on command specifier 0x{timeout.CommandSpecifier:X2}.", Attribution.Device);
            }
            finally
            {
                try
                {
                    await session.Lss.SwitchStateGlobalAsync(LssState.Waiting, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    book.Note("LSS-03 cleanup", ex);
                }
            }
        }).ConfigureAwait(false);

        await book.RunAsync("STO-01", "store", "1010h/1011h save and restore parameters", Tier.Read, "CiA 301 1010h/1011h", async () =>
        {
            // Read-only here on purpose: what 1010h:01 reports is whether the device can store on
            // command, and that is worth knowing without writing anything. STO-02 is the write.
            if (!session.MayTransfer(0x1010, 0x01))
                return Outcome.NeedsSetup(session.GateReason(0x1010, 0x01));

            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, 0x1010, 0x01, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4)
                    return Outcome.Fail($"1010h:01 is UNSIGNED32, the device returned {IdentityAndSdoChecks.Hex(raw)}.");

                var capability = BitConverter.ToUInt32(raw, 0);
                var measurements = new Dictionary<string, string>
                {
                    ["1010h:01"] = $"0x{capability:X8}",
                    ["saves on command"] = ((capability & 1) != 0).ToString(),
                    ["saves autonomously"] = ((capability & 2) != 0).ToString(),
                };

                if (session.MayTransfer(0x1011, 0x01))
                {
                    var restore = await session.Client.SdoUploadAsync(session.Target, 0x1011, 0x01, cancellationToken).ConfigureAwait(false);
                    if (restore.Length == 4)
                    {
                        var restoreCapability = BitConverter.ToUInt32(restore, 0);
                        measurements["1011h:01"] = $"0x{restoreCapability:X8}";
                        measurements["restores defaults on command"] = ((restoreCapability & 1) != 0).ToString();
                    }
                }

                return Outcome.Pass("Storage capability read.", measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 1010h; storing parameters is optional in CiA 301.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("STO-02", "store", "Store parameters on command (1010h:01 = 'save')", Tier.Commission, "CiA 301 1010h", async () =>
        {
            if (!session.MayTransfer(0x1010, 0x01))
                return Outcome.NeedsSetup(session.GateReason(0x1010, 0x01));

            uint capability;
            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, 0x1010, 0x01, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4) return Outcome.NeedsSetup("1010h:01 did not read back as UNSIGNED32; see STO-01.");
                capability = BitConverter.ToUInt32(raw, 0);
            }
            catch (SdoAbortException)
            {
                return Outcome.NotApplicable("The device has no 1010h.");
            }

            if ((capability & 1) == 0)
            {
                return Outcome.NotApplicable(
                    $"1010h:01 reports 0x{capability:X8}: the device does not save on command, so the signature "
                    + "write is not something it is required to accept.");
            }

            // "save" in ASCII, little-endian as CiA 301 specifies the signature. Nothing else is
            // accepted, and that is the point of the signature: a stray write cannot store.
            var save = new byte[] { 0x73, 0x61, 0x76, 0x65 };
            await session.Client.SdoDownloadAsync(session.Target, 0x1010, 0x01, save, cancellationToken).ConfigureAwait(false);
            return Outcome.Pass(
                "The device accepted the 'save' signature on 1010h:01. Whatever its parameters were at this moment "
                + "is now what it will come up with after a power cycle.",
                new Dictionary<string, string> { ["signature written"] = "73 61 76 65 ('save')" });
        }).ConfigureAwait(false);

        await book.RunAsync("STO-03", "store", "A wrong signature on 1010h:01 is refused", Tier.Write, "CiA 301 1010h", async () =>
        {
            // Write tier, not commission: a correctly implemented device stores nothing here. The
            // signature is deliberately not "save", so a device that accepts it has a defect that
            // is worth finding before a stray write finds it.
            if (!session.MayTransfer(0x1010, 0x01))
                return Outcome.NeedsSetup(session.GateReason(0x1010, 0x01));

            var wrong = new byte[] { 0x6E, 0x6F, 0x70, 0x65 }; // "nope"
            try
            {
                await session.Client.SdoDownloadAsync(session.Target, 0x1010, 0x01, wrong, cancellationToken).ConfigureAwait(false);
                return Outcome.Fail(
                    "The device accepted 'nope' as the 1010h:01 signature. CiA 301 requires the signature 'save' and "
                    + "nothing else; this device can be made to store its parameters by any write to 1010h:01.");
            }
            catch (SdoAbortException abort) when (abort.Origin == SdoAbortOrigin.Peer)
            {
                var measurements = new Dictionary<string, string> { ["abort code"] = $"0x{abort.AbortCode:X8}" };
                return abort.AbortCode == (uint)SdoAbortCode.DataCannotBeTransferred
                    ? Outcome.Pass("Refused with 0x08000020, as CiA 301 specifies for a wrong signature.", measurements)
                    : Outcome.Inconclusive(
                        $"Refused with 0x{abort.AbortCode:X8} rather than 0x08000020. The refusal is what matters; "
                        + "the code is not the one CiA 301 names.", Attribution.Device, measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 1010h.");
            }
        }).ConfigureAwait(false);
    }
}
