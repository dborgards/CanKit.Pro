using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Consumer side (CiA DSP 304 V1.0 §8.1.1, §8.1.3.1, §9.5) and the global failsafe
/// command (§8.2).</summary>
internal sealed partial class SrdoEngine
{
    /// <summary>Routes a frame to the SRDO it belongs to. True when the id is one of this node's
    /// SRDO ids or 001h, whether or not anything happened: a remote frame (§8.1 "RTR is not
    /// possible"), an echo of our own producer frames (#95), a frame outside Operational.</summary>
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr)
    {
        if (_disposed) return false;
        if (cobId == CanOpenCobId.GlobalFailsafeCommand)
        {
            // §8.2.3 Write GFC: L = 0. §8.4.2.2 1300h: "0: GFC is not valid".
            if (!isRtr && data.Length == 0 && _operational && IsGfcEnabled()) _host.GlobalFailsafeCommandReceived();
            return true;
        }
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is not { Direction: not SrdoDirection.None } rt) continue;
            if (cobId != rt.CobId1 && cobId != rt.CobId2) continue;
            if (isRtr || rt.Direction == SrdoDirection.Transmit || !_operational || !rt.ConfigurationValid) return true;
            if (cobId == rt.CobId1) OnFirstFrame(rt, data);
            else OnSecondFrame(rt, data);
            return true;
        }
        return false;
    }

    /// <summary>§8.2.2 push model, unconfirmed. False when 1300h ≠ 1 or not Operational.</summary>
    public bool TrySendGfc()
    {
        if (_disposed || !_operational || !IsGfcEnabled()) return false;
        _host.Send(CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>());
        return true;
    }

    private bool IsGfcEnabled() => _od.TryReadUnsigned(SrdoRecords.GfcParameter, 0x00, out var v) && v == 1;

    private void ArmConsumer(SrdoRuntime rt)
    {
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
            // §9.5: "received in chronological order (high priority identifier first)".
            Invalidate(rt, SrdoInvalidReason.OutOfOrder);
            return;
        }
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
        ArmSct(rt);
        Actuate(rt, first);
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

    /// <summary>"If L exceeds the number 'n' … only the first 'n' bytes are used" (§8.1.3.1).
    /// Writes under the OD lock, like the RPDO path; a rejected write is reported, not swallowed.</summary>
    private void Actuate(SrdoRuntime rt, byte[] payload)
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
            }
            offset += entry.ByteLength;
        }
    }
}
