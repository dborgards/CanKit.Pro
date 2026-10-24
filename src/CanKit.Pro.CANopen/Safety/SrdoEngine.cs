using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The SRDO runtime of a node (CiA DSP 304 V1.0 §8.1, §8.2): one producer or consumer per
/// record 1301h–1340h, derived from the records like the PDO engine derives its PDOs. Owned
/// and driven by <see cref="CanOpenNode"/> on its actor loop; every member except
/// <see cref="GetState"/> and <see cref="CollectChangeOfStateEntries"/> is actor-only.
/// Cycles run on <see cref="IProtocolActor.Schedule"/>, the SCT and SRVT on
/// <see cref="IDeadlineScheduler"/>, so a <c>ManualTimeSource</c> drives all of it in tests.
/// </summary>
internal sealed partial class SrdoEngine : IDisposable
{
    private readonly IProtocolActor _actor;
    private readonly IDeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od;
    private byte _nodeId;
    private readonly ISrdoEngineHost _host;
    private readonly SrdoRuntime?[] _runtimes;
    private readonly SrdoState[] _snapshots;
    private bool _operational;
    private bool _disposed;

    public SrdoEngine(IProtocolActor actor, ITimeSource time, IDeadlineScheduler deadlines, ObjectDictionary od,
        byte nodeId, int srdoCount, ISrdoEngineHost host)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        // The node's time source: the engine reads no monotonic time itself — cycles run on the
        // actor's scheduler and SCT/SRVT on the deadline scheduler, both driven by this source —
        // so it is only checked. LastValidAt is wall-clock UTC (DateTime.UtcNow in SetValid).
        _ = time ?? throw new ArgumentNullException(nameof(time));
        _deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
        _od = od ?? throw new ArgumentNullException(nameof(od));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (srdoCount is < 0 or > SrdoRecords.MaxSrdoCount) throw new ArgumentOutOfRangeException(nameof(srdoCount));
        _nodeId = nodeId;
        SrdoCount = srdoCount;
        _runtimes = new SrdoRuntime?[srdoCount + 1];
        _snapshots = new SrdoState[srdoCount + 1];
        for (int n = 1; n <= srdoCount; n++)
            _snapshots[n] = new SrdoState(n, SrdoDirection.None, false, null, null);
    }

    public int SrdoCount { get; }
    public bool IsOperational => _operational;

    /// <summary>The node-id the first-cycle delay is computed from. Actor only.</summary>
    internal void SetNodeId(byte nodeId) => _nodeId = nodeId;

    /// <summary>Snapshot for any thread.</summary>
    public SrdoState GetState(int srdoNumber)
    {
        CheckRange(srdoNumber);
        return Volatile.Read(ref _snapshots[srdoNumber]);
    }

    private void CheckRange(int srdoNumber)
    {
        if (srdoNumber < 1 || srdoNumber > SrdoCount)
            throw new ArgumentOutOfRangeException(nameof(srdoNumber), srdoNumber, $"SRDO number must be 1..{SrdoCount}.");
    }

    /// <summary>Reads record n and brings its runtime in line: configuration validity
    /// (§9.5 last rule), direction, times, ids and mapping. Re-arms when Operational.</summary>
    public void Rebuild(int srdoNumber)
    {
        CheckRange(srdoNumber);
        if (_disposed) return;
        var rt = _runtimes[srdoNumber] ??= new SrdoRuntime(srdoNumber);
        Disarm(rt);
        if (SrdoRecords.TryReadCommunication(_od, srdoNumber, out var p))
        {
            rt.Direction = p.Direction;
            rt.CycleTime = p.RefreshOrSafeguardCycleTime;
            rt.ValidationTime = p.ValidationTime;
            rt.CobId1 = p.CobId1;
            rt.CobId2 = p.CobId2;
        }
        else
        {
            rt.Direction = SrdoDirection.None;
        }
        rt.Mapping = SrdoRecords.ReadMapping(_od, srdoNumber);
        rt.TotalBytes = 0;
        foreach (var e in rt.Mapping) rt.TotalBytes += e.ByteLength;
        rt.ConfigurationValid = rt.Direction != SrdoDirection.None && SrdoRecords.IsConfigurationValid(_od, srdoNumber);
        EndShortFrameRun(rt);
        if (_operational) Arm(rt);
        else SetInvalid(rt, rt.Direction == SrdoDirection.None ? null : SrdoInvalidReason.NotOperational);
    }

    /// <summary>§8.3.2.2: "Safety communication is only supported in this state." Every SRDO is
    /// re-read — 13FEh/13FFh may have changed in Pre-Operational — and armed.</summary>
    public void EnterOperational()
    {
        if (_disposed) return;
        _operational = true;
        for (int n = 1; n <= SrdoCount; n++) Rebuild(n);
    }

    /// <summary>Cycles stop, deadlines go, every existing SRDO is NotOperational.</summary>
    public void LeaveOperational()
    {
        _operational = false;
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is not { } rt) continue;
            Disarm(rt);
            SetInvalid(rt, rt.Direction == SrdoDirection.None ? null : SrdoInvalidReason.NotOperational);
        }
    }

    /// <summary>§8.1: "SRDOs may also be transmitted event-driven". Only a valid producer
    /// transmits; the cycle restarts from this transmission.</summary>
    public void Trigger(int srdoNumber)
    {
        CheckRange(srdoNumber);
        if (_disposed || !_operational) return;
        // IsValid: a producer stopped by a mapped object it cannot read stays silent (MappedObjectUnavailable).
        if (_runtimes[srdoNumber] is { Direction: SrdoDirection.Transmit, ConfigurationValid: true, IsValid: true } rt) Transmit(rt);
    }

    /// <summary>The OD entries mapped in a transmit SRDO, for the node's change-of-state
    /// pre-filter. Reads the records, not the runtime: it runs on the writing thread.</summary>
    public void CollectChangeOfStateEntries(HashSet<uint> keys, Func<ushort, byte, uint> key)
    {
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (!SrdoRecords.TryReadCommunication(_od, n, out var p) || p.Direction != SrdoDirection.Transmit) continue;
            foreach (var e in SrdoRecords.ReadMapping(_od, n)) keys.Add(key(e.Index, e.Subindex));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operational = false;
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is { } rt) Disarm(rt);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Producer (§8.1.3.1 Figure 4, §9.5).
    // -----------------------------------------------------------------------------------------

    private void Arm(SrdoRuntime rt)
    {
        if (rt.Direction == SrdoDirection.None)
        {
            SetInvalid(rt, null);
            return;
        }
        if (!rt.ConfigurationValid)
        {
            // §8.3.1 step D: "In case of mismatch the safety node shall not transmit SRDOs; the
            // safety controller shall enter (stay) in safe state."
            SetInvalid(rt, SrdoInvalidReason.ConfigurationInvalid);
            return;
        }
        if (rt.Direction == SrdoDirection.Transmit)
        {
            SetValid(rt);
            // §9.5: "The first cyclic transmit of an SRDO shall be delayed for 0.5 ms * Node-ID."
            ScheduleCycle(rt, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * _nodeId / 2));
            return;
        }
        ArmConsumer(rt);
    }

    private void Disarm(SrdoRuntime rt)
    {
        rt.Generation++;
        rt.CycleHandle?.Dispose();
        rt.CycleHandle = null;
        DisarmConsumer(rt);
    }

    private void ScheduleCycle(SrdoRuntime rt, TimeSpan delay)
    {
        int generation = ++rt.Generation;
        rt.CycleHandle?.Dispose();
        rt.CycleHandle = _actor.Schedule(delay, () =>
        {
            if (_disposed || !_operational || generation != rt.Generation) return;
            if (!ReferenceEquals(_runtimes[rt.Number], rt) || !rt.ConfigurationValid) return;
            Transmit(rt);
        });
    }

    /// <summary>Both frames as one unit handed to the host — "the redundant transmission is sent
    /// after the first transmission to the CAN controller with minimum delay" (§8.1) — then the
    /// refresh cycle restarts, so the refresh time is the maximum interval between transmissions.
    /// A pair due while the previous one is still being sent waits as the host's pending pair
    /// and is replaced by a later one (<see cref="ISrdoEngineHost.SendPair"/>); a send that
    /// throws is reported and the cycle goes on.</summary>
    private void Transmit(SrdoRuntime rt)
    {
        if (BuildPayload(rt) is not { } payload)
        {
            // Not a pair padded or cut to fit: no pair at all, and no further cycle until the
            // SRDO is rebuilt. Consumers see the silence as an SCT expiry.
            Disarm(rt);
            SetInvalid(rt, SrdoInvalidReason.MappedObjectUnavailable);
            return;
        }
        try
        {
            _host.SendPair(rt.Number, rt.CobId1, payload, rt.CobId2, SrdoFrames.Invert(payload));
        }
        catch (Exception ex)
        {
            // A transport failure is reported like every other send failure; it must not end the
            // cycle, and the SRDO stays valid.
            _host.ReportBackgroundException(ex);
        }
        finally
        {
            ScheduleCycle(rt, rt.CycleTime);
        }
    }

    /// <summary>The mapped objects at the lengths the mapping gives them (§8.1.3.1), or null —
    /// reported — when one is missing, has no fixed width or no longer has the width of its
    /// entry: it was re-declared after the configuration was committed, and neither the CRC nor
    /// the records cover a declaration. An SRDO maps fixed-width objects only (the dictionary's
    /// validator refuses the others), so this is the defence behind that rule: nothing is ever
    /// cut or padded to fit.</summary>
    private byte[]? BuildPayload(SrdoRuntime rt)
    {
        var payload = new byte[rt.TotalBytes];
        int offset = 0;
        foreach (var entry in rt.Mapping)
        {
            if (!_od.TryGet(entry.Index, entry.Subindex, out var od))
            {
                ReportUnavailable(rt, entry, "does not exist");
                return null;
            }
            var raw = od.GetRawValue();
            if (!OdEntryLayout.HasFixedSize(od.DataType))
            {
                ReportUnavailable(rt, entry, $"is {od.DataType}, which has no fixed width");
                return null;
            }
            int fixedSize = OdEntryLayout.FixedSize(od.DataType);
            if (fixedSize != entry.ByteLength)
            {
                ReportUnavailable(rt, entry, $"is {od.DataType} ({fixedSize} byte(s)), not the {entry.ByteLength} byte(s) mapped");
                return null;
            }
            Buffer.BlockCopy(raw, 0, payload, offset, entry.ByteLength);
            offset += entry.ByteLength;
        }
        return payload;
    }

    private void ReportUnavailable(SrdoRuntime rt, PdoMappingEntry entry, string what)
        => _host.ReportBackgroundException(new InvalidOperationException(
            $"SRDO{rt.Number}: the mapped object 0x{entry.Index:X4}:{entry.Subindex:X2} {what}; the producer transmits nothing until the SRDO is rebuilt (CiA DSP 304 §8.1.3.1)."));

    // -----------------------------------------------------------------------------------------
    // State (transitions only are reported; the snapshot is for any thread).
    // -----------------------------------------------------------------------------------------

    private void SetValid(SrdoRuntime rt)
    {
        bool changed = !rt.IsValid;
        rt.IsValid = true;
        rt.Reason = null;
        rt.LastValidAt = DateTime.UtcNow;
        Publish(rt);
        if (changed) _host.SrdoStateChanged(rt.Number, true, null);
    }

    private void SetInvalid(SrdoRuntime rt, SrdoInvalidReason? reason)
    {
        bool wasValid = rt.IsValid;
        bool changed = wasValid || rt.Reason != reason;
        rt.IsValid = false;
        rt.Reason = reason;
        Publish(rt);
        // A null reason means the SRDO no longer exists (direction None): that is still a
        // valid-to-invalid transition and is reported; an SRDO that was never valid is not.
        if (changed && (reason is not null || wasValid)) _host.SrdoStateChanged(rt.Number, false, reason);
    }

    private void EndShortFrameRun(SrdoRuntime rt)
    {
        if (!rt.ShortFrameReported) return;
        rt.ShortFrameReported = false;
        _host.EmcyCauseEnded(rt.Number);
    }

    private void Publish(SrdoRuntime rt)
        => Volatile.Write(ref _snapshots[rt.Number], new SrdoState(rt.Number, rt.Direction, rt.IsValid, rt.Reason, rt.LastValidAt));

    private sealed class SrdoRuntime
    {
        public SrdoRuntime(int number) => Number = number;
        public int Number { get; }
        public SrdoDirection Direction { get; set; }
        public TimeSpan CycleTime { get; set; }
        public TimeSpan ValidationTime { get; set; }
        public uint CobId1 { get; set; }
        public uint CobId2 { get; set; }
        public PdoMappingEntry[] Mapping { get; set; } = Array.Empty<PdoMappingEntry>();
        public int TotalBytes { get; set; }
        public bool ConfigurationValid { get; set; }
        public bool IsValid { get; set; }
        public SrdoInvalidReason? Reason { get; set; }
        public DateTime? LastValidAt { get; set; }
        public int Generation;
        public IDisposable? CycleHandle { get; set; }
        // Consumer.
        public byte[]? Pending { get; set; }
        public IDeadline? SctDeadline { get; set; }
        public IDeadline? SrvtDeadline { get; set; }
        public bool ShortFrameReported { get; set; }
        /// <summary>A plain frame followed by an inverted one since the consumer was armed,
        /// whatever became of the pair (#360, S-7).</summary>
        public bool PairSeen { get; set; }
    }
}
