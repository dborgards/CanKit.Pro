using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Consumer side (CiA DSP 304 V1.0 §8.1.1, §8.1.3.1, §9.5) and the global failsafe
/// command (§8.2).</summary>
internal sealed partial class SrdoEngine
{
    /// <summary>Routes a frame to the SRDO it belongs to. True when the id is one of this node's
    /// SRDO ids or 001h, whether or not anything happened: a remote frame (§8.1 "RTR is not
    /// possible"), an echo of our own producer frames (#95), a frame outside Operational.
    /// <paramref name="isFd"/>: the frame arrived as a CAN FD frame, which is neither an SRDO frame
    /// nor a GFC (§8.1, §8.2: classic CAN).</summary>
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr, bool isFd = false)
    {
        if (_disposed) return false;
        // A node without SRDOs is no safety node: an application-declared 1300h on it is the
        // application's object, and 001h is not this engine's frame.
        if (cobId == CanOpenCobId.GlobalFailsafeCommand && SrdoCount > 0)
        {
            // §8.2.3 Write GFC: L = 0, a classic CAN frame (§8.2) — a CAN FD frame is no GFC.
            // EN 50325-5:2010 §7.2.1 (OBE-12450): the GFC consumer signals the reception to its
            // SRLD, whatever 1300h holds — §6.4.2.1 (OBE-12439) makes 1300h the record of the
            // indication, not a switch in front of it (#289).
            if (!isRtr && !isFd && data.Length == 0 && _operational)
            {
                MarkGfc();
                _host.GlobalFailsafeCommandReceived();
            }
            return true;
        }
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is not { Direction: not SrdoDirection.None } rt) continue;
            if (cobId != rt.CobId1 && cobId != rt.CobId2) continue;
            if (isRtr || rt.Direction == SrdoDirection.Transmit || !_operational || !rt.ConfigurationValid) return true;
            // §8.1: an SRDO is a classic CAN frame. A CAN FD frame, or a payload over 8 bytes, on
            // either id is no SRDO frame: rejected as a wrong inverse is — the pending first frame
            // dropped, nothing actuated, the SCT left running, no EMCY (8210h is for a pair shorter
            // than the mapping). "Only the first 'n' bytes are used" (§8.1.3.1) applies within 8.
            if (isFd || data.Length > 8)
            {
                Invalidate(rt, SrdoInvalidReason.Mismatch);
                return true;
            }
            if (cobId == rt.CobId1) OnFirstFrame(rt, data);
            else OnSecondFrame(rt, data);
            return true;
        }
        return false;
    }

    /// <summary>§8.2.2 push model, unconfirmed. False when not Operational. EN 50325-5:2010
    /// §6.4.2.1 (OBE-12437): the request sets 1300h to 01 — the value is no precondition (#289).</summary>
    public bool TrySendGfc()
    {
        if (_disposed || !_operational) return false;
        MarkGfc();
        _host.Send(CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>());
        return true;
    }

    /// <summary>EN 50325-5:2010 §6.4.2.1 (OBE-12437, OBE-12439): 1300h is 01 once this SRD has
    /// requested a GFC or been indicated one. The value stays until the application or a tool
    /// writes it back — the norm names no event that clears it. A node whose description leaves
    /// the optional 1300h out has nothing to set.</summary>
    private void MarkGfc()
    {
        if (!_od.TryReadUnsigned(SrdoRecords.GfcParameter, 0x00, out var current) || current == 1) return;
        try { _od.WriteRaw(SrdoRecords.GfcParameter, 0x00, new byte[] { 1 }); }
        catch (ArgumentException ex) // the entry exists (read above); WriteRaw refuses a value by this exception
        {
            _host.ReportBackgroundException(new InvalidOperationException($"1300h could not be set to 01: {ex.Message}", ex));
        }
    }

    private void ArmConsumer(SrdoRuntime rt)
    {
        rt.PairSeen = false;
        SetInvalid(rt, SrdoInvalidReason.NotReceived);
        ArmSct(rt);
    }

    private void DisarmConsumer(SrdoRuntime rt)
    {
        rt.Pending = null;
        rt.SctDeadline?.Dispose();
        rt.SctDeadline = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
    }

    /// <summary>Figure 2: the safeguard cycle time "shall be survived by the safety controller".</summary>
    private void ArmSct(SrdoRuntime rt)
    {
        rt.SctDeadline?.Dispose();
        // A callback that lost the race with Dispose()/re-arm is neutralised by the deadline's own
        // cancelled/expired state, so no staleness guard is needed here beyond _disposed.
        rt.SctDeadline = _deadlines.Arm(rt.CycleTime, () =>
        {
            if (_disposed) return;
            rt.SctDeadline = null;
            Invalidate(rt, SrdoInvalidReason.SafeguardCycleExpired);
        });
    }

    private void OnFirstFrame(SrdoRuntime rt, byte[] data)
    {
        // A second first frame replaces the pending one; Figure 3: the SRVT runs from the
        // first frame to its second.
        rt.Pending = data;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = _deadlines.Arm(rt.ValidationTime, () =>
        {
            if (_disposed) return;
            rt.SrvtDeadline = null;
            rt.Pending = null;
            Invalidate(rt, SrdoInvalidReason.ValidationTimeExpired);
        });
    }

    private void OnSecondFrame(SrdoRuntime rt, byte[] data)
    {
        if (rt.Pending is not { } first)
        {
            // §9.5: "received in chronological order (high priority identifier first)" — a rule
            // about a pair. While the SRDO is still NotReceived as armed — no pair seen, nothing
            // found wrong since — an inverted frame with no plain one pending belongs to no pair
            // of this consumer's: a producer already running when the consumer entered
            // Operational between its plain and its inverted frame. That is no violation to
            // report — the SRDO stays NotReceived, its SCT runs on, and the next pair makes it
            // valid (#360). After a pair, an expiry or a mismatch, a lone inverted frame is
            // OutOfOrder. "A pair" is any plain frame followed by its inverted one, processed or
            // not: a short pair leaves the reason at NotReceived (§8.1.3.1, EMCY 8210h only) but
            // has shown the producer's order, so the reason alone cannot say "no pair yet".
            if (!rt.PairSeen && rt.Reason == SrdoInvalidReason.NotReceived) return;
            Invalidate(rt, SrdoInvalidReason.OutOfOrder);
            return;
        }
        rt.PairSeen = true;
        rt.Pending = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
        if (!SrdoFrames.IsInversePair(first, data))
        {
            Invalidate(rt, SrdoInvalidReason.Mismatch);
            return;
        }
        // §8.1.3.1: "If L is less than 'n' the data of the received SRDO is not processed and an
        // Emergency message with error code 8210h shall be produced" — once per run, like RPDOs.
        if (first.Length < rt.TotalBytes)
        {
            if (!rt.ShortFrameReported)
            {
                rt.ShortFrameReported = true;
                _host.EmitEmcy(0x8210);
            }
            return;
        }
        rt.ShortFrameReported = false;
        // Data the consumer cannot use is not received, as §8.1.3.1 leaves a short pair
        // unprocessed: a pair a mapped object refused neither validates the SRDO nor restarts the
        // SCT, which runs on from the last pair taken.
        if (!Actuate(rt, first))
        {
            Invalidate(rt, SrdoInvalidReason.ActuationRefused);
            return;
        }
        ArmSct(rt);
        SetValid(rt);
        _host.SrdoReceived(rt.Number, rt.CobId1, first);
    }

    private void Invalidate(SrdoRuntime rt, SrdoInvalidReason reason)
    {
        rt.Pending = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
        SetInvalid(rt, reason);
    }

    /// <summary>"If L exceeds the number 'n' … only the first 'n' bytes are used" (§8.1.3.1) —
    /// within the 8 bytes of a classic CAN frame; a longer payload never gets here.
    /// Writes under the OD lock, like the RPDO path. A rejected write is reported, not swallowed,
    /// and ends the pair there: false, with the objects before it written and the rest untouched.</summary>
    private bool Actuate(SrdoRuntime rt, byte[] payload)
    {
        int offset = 0;
        foreach (var entry in rt.Mapping)
        {
            var chunk = new byte[entry.ByteLength];
            Buffer.BlockCopy(payload, offset, chunk, 0, entry.ByteLength);
            try { _od.WriteRaw(entry.Index, entry.Subindex, chunk); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
            {
                _host.ReportBackgroundException(new InvalidOperationException(
                    $"SRDO{rt.Number}: the mapped object 0x{entry.Index:X4}:{entry.Subindex:X2} rejected {entry.ByteLength} byte(s): {ex.Message}", ex));
                return false;
            }
            offset += entry.ByteLength;
        }
        return true;
    }
}
