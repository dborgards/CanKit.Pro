using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

// The application seam: device profiles that attach to the node when it opens
// (CanOpenNodeOptions.Applications). Attaching, the claims on object indices, and the host the
// node gives each application live here.
internal sealed partial class CanOpenNode
{
    // The lowest index an application may claim: below it lie the communication profile and the
    // reserved areas, which belong to the node.
    private const ushort FirstClaimableIndex = 0x2000;

    // One per attached application, in attach order. Written once, on the constructing thread,
    // before the node's actor runs anything; empty for a node opened without applications.
    private ApplicationSlot[] _applications = Array.Empty<ApplicationSlot>();

    private sealed class ApplicationSlot
    {
        internal ApplicationSlot(ICanOpenApplication application, ApplicationHost host)
        {
            Application = application;
            Host = host;
        }

        internal ICanOpenApplication Application { get; }

        internal ApplicationHost Host { get; }

        // Copied when the node opens: the claims are read once, so a list the application changes
        // later cannot move what the node enforces.
        internal CanOpenIndexRange[] Claims { get; init; } = Array.Empty<CanOpenIndexRange>();
    }

    /// <summary>Whether an attached application claims <paramref name="index"/>.</summary>
    private bool IsClaimed(ushort index) => ClaimantOf(index) is not null;

    /// <summary>The application that claims <paramref name="index"/>, or <see langword="null"/>.</summary>
    private ApplicationSlot? ClaimantOf(ushort index)
    {
        var applications = _applications;
        for (int i = 0; i < applications.Length; i++)
        {
            foreach (var range in applications[i].Claims)
            {
                if (range.Contains(index)) return applications[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Validates the claims and requirements of <see cref="CanOpenNodeOptions.Applications"/>, then
    /// attaches each application in list order. Runs in the constructor, inside the guard that ends
    /// the actor on a throw, after the dictionary holds the communication profile and the device
    /// description, and before the node boots.
    /// </summary>
    /// <exception cref="ArgumentException">A claim or requirement cannot be met; the parameter is
    /// <c>options</c> and the message names the application.</exception>
    private void AttachApplications()
    {
        var listed = _options.Applications;
        if (listed.Count == 0) return;

        var slots = new ApplicationSlot[listed.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var application = listed[i] ?? throw OptionsError($"CanOpenNodeOptions.Applications[{i}] is null.");
            for (int earlier = 0; earlier < i; earlier++)
            {
                if (ReferenceEquals(listed[earlier], application))
                    throw OptionsError($"Application '{application.Name}' is listed twice in CanOpenNodeOptions.Applications.");
            }
            var host = new ApplicationHost(this);
            slots[i] = new ApplicationSlot(application, host)
            {
                Claims = ValidatedClaims(application, slots, i),
            };
            host.Slot = slots[i];
            ValidateRequirements(application);
        }

        _applications = slots;
        foreach (var slot in slots) slot.Application.Attach(slot.Host);

        // What the applications declared is part of the power-on state, like what the description
        // declared: Reset Node returns it to this, and 1010h saves it.
        _factoryDefaults = SnapshotRestorableValues();
        _powerOnValues = _factoryDefaults;
    }

    private static CanOpenIndexRange[] ValidatedClaims(ICanOpenApplication application, ApplicationSlot[] earlier, int count)
    {
        var claims = new CanOpenIndexRange[application.ClaimedRanges.Count];
        for (int r = 0; r < claims.Length; r++)
        {
            var range = application.ClaimedRanges[r];
            if (range.First < FirstClaimableIndex)
            {
                throw OptionsError($"Application '{application.Name}' claims {range}, which reaches below 0x{FirstClaimableIndex:X4}: " +
                    "the communication profile and the reserved areas belong to the node.");
            }
            for (int other = 0; other < r; other++)
            {
                if (range.Overlaps(claims[other]))
                    throw OptionsError($"Application '{application.Name}' claims {claims[other]} and {range}, which overlap.");
            }
            for (int e = 0; e < count; e++)
            {
                foreach (var taken in earlier[e].Claims)
                {
                    if (range.Overlaps(taken))
                    {
                        throw OptionsError($"Applications '{earlier[e].Application.Name}' and '{application.Name}' claim overlapping " +
                            $"ranges: {taken} and {range}.");
                    }
                }
            }
            claims[r] = range;
        }
        return claims;
    }

    private void ValidateRequirements(ICanOpenApplication application)
    {
        var requirements = application.Requirements;
        if (requirements.PdoCount > _pdoCount)
        {
            throw OptionsError($"Application '{application.Name}' configures {requirements.PdoCount} PDOs: " +
                $"set CanOpenNodeOptions.PdoCount to at least {requirements.PdoCount} (it is {_pdoCount}).");
        }
        if (requirements.CommunicationFailureMonitoring && !_options.EnableErrorBehavior)
        {
            throw OptionsError($"Application '{application.Name}' monitors communication failures: " +
                "set CanOpenNodeOptions.EnableErrorBehavior to true.");
        }
    }

    private static ArgumentException OptionsError(string message) => new(message, "options");

    // =========================================================================================
    // Written entries: what the claimant is told, and when.
    // =========================================================================================

    // The entries written since the claimant was last told, as CosKey values, under
    // _applicationGate; and whether an evaluation of them is already queued. Same mechanism as
    // the change-of-state filter (_cosDirty): the actor's mailbox is unbounded, so a writer that
    // writes in a loop must not post per write.
    private readonly object _applicationGate = new();
    private HashSet<uint>? _applicationDirty;
    private bool _applicationEvaluationPosted;

    // The application whose callback the actor loop is running right now, or null. Actor-confined:
    // set and cleared by InvokeApplication, read only after IsOnCurrentActor has said so.
    private ApplicationSlot? _applicationInCallback;

    /// <summary>
    /// EntryWritten, on the writing thread and under the dictionary's write gate: a stored write to
    /// an entry an application claims. Records the entry and queues one evaluation, which tells the
    /// claimant on the actor loop (<see cref="EvaluateApplicationEntriesOnActor"/>). Not for
    /// what the claimant writes from inside its own callback, and not for a restore of values in
    /// a reset: neither is news to it.
    /// </summary>
    private void OnOdEntryWrittenForApplications(ushort index, byte subindex)
    {
        if (_applications.Length == 0) return;
        // RestoreValues sets this under the write gate, which this write holds too: it is true for
        // the restore's own writes and for nobody else's.
        if (_restoringValues) return;
        var claimant = ClaimantOf(index);
        if (claimant is null) return;
        // The claimant's own write from inside one of its callbacks. IsOnCurrentActor first: the
        // field belongs to the actor loop, and a write from another thread is never that.
        if (_actor.IsOnCurrentActor && ReferenceEquals(_applicationInCallback, claimant)) return;
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_applicationGate)
        {
            (_applicationDirty ??= new HashSet<uint>()).Add(CosKey(index, subindex));
            if (_applicationEvaluationPosted) return;
            _applicationEvaluationPosted = true;
        }

        try
        {
            _actor.Post(EvaluateApplicationEntriesOnActor);
        }
        catch (ObjectDisposedException)
        {
            lock (_applicationGate)
            {
                _applicationEvaluationPosted = false;
            }
        }
    }

    /// <summary>
    /// Tells each claimant the entries written since it was last told: sorted by index, then
    /// sub-index, each once. Actor loop. Writes landing while this runs re-arm the dirty set and
    /// queue the next evaluation, so none is lost; one claimant that throws does not keep the next
    /// from being told (<see cref="InvokeApplication"/>).
    /// </summary>
    private void EvaluateApplicationEntriesOnActor()
    {
        HashSet<uint>? dirty;
        lock (_applicationGate)
        {
            dirty = _applicationDirty;
            _applicationDirty = null;
            _applicationEvaluationPosted = false;
        }
        if (dirty is null || dirty.Count == 0 || Volatile.Read(ref _disposed) != 0) return;

        var keys = new uint[dirty.Count];
        dirty.CopyTo(keys);
        Array.Sort(keys); // CosKey is index << 8 | sub-index: its order is index, then sub-index

        foreach (var slot in _applications)
        {
            List<CanOpenObjectAddress>? entries = null;
            foreach (var key in keys)
            {
                var index = (ushort)(key >> 8);
                if (!ReferenceEquals(ClaimantOf(index), slot)) continue;
                (entries ??= new List<CanOpenObjectAddress>()).Add(new CanOpenObjectAddress(index, (byte)(key & 0xFF)));
            }
            if (entries is null) continue;
            var written = entries.ToArray();
            InvokeApplication(slot, application => application.OnEntriesWritten(written));
        }
    }

    // =========================================================================================
    // Callbacks into an application.
    // =========================================================================================

    /// <summary>
    /// Asks the claimant to decide a write. Writing thread, under the dictionary's write gate: on an
    /// SDO download or an RPDO that thread is the actor loop, so a throw must not travel through the
    /// dictionary into the SDO server or the PDO engine. It is reported as a background exception,
    /// and the write is refused with a general error.
    /// </summary>
    private OdWriteDecision ValidateApplicationWrite(ApplicationSlot slot, ushort index, byte subindex, byte[] value)
    {
        try
        {
            return slot.Application.ValidateWrite(index, subindex, value);
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
            return OdWriteDecision.Reject(SdoAbortCode.General);
        }
    }

    /// <summary>
    /// Runs <paramref name="callback"/> against the application of <paramref name="slot"/>: the one
    /// way the node calls into an application on the actor loop (every callback but
    /// <see cref="ICanOpenApplication.Attach"/> and <see cref="ICanOpenApplication.ValidateWrite"/>).
    /// Marks the application as being in a callback, so that what it writes there is not reported
    /// back to it, and reports anything it throws as a background exception: one application's
    /// failure stops neither the node nor the application after it.
    /// </summary>
    private void InvokeApplication(ApplicationSlot slot, Action<ICanOpenApplication> callback)
    {
        // Restored rather than cleared: a callback that makes the node run another one (a state
        // request, say) leaves the outer one still in progress.
        var outer = _applicationInCallback;
        _applicationInCallback = slot;
        try
        {
            callback(slot.Application);
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
        }
        finally
        {
            _applicationInCallback = outer;
        }
    }

    /// <summary>What one application is given of the node. Members other than the dictionary, the
    /// state, the clock and the deadlines arrive with the callbacks that need them.</summary>
    private sealed class ApplicationHost : ICanOpenApplicationHost
    {
        private readonly CanOpenNode _node;

        internal ApplicationHost(CanOpenNode node) => _node = node;

        // The slot this host belongs to, set right after the slot is built and before the node
        // calls Attach or anything else on the application. A deadline or a request reads it when
        // it runs, never earlier.
        internal ApplicationSlot Slot { get; set; } = null!;

        public ObjectDictionary ObjectDictionary => _node._od;

        public NmtState State => _node._state;

        public TimeSpan Now
        {
            get
            {
                var time = _node._actor.TimeSource;
                return TickMath.ElapsedFromTicks(time.GetTimestamp(), time.Frequency);
            }
        }

        public bool CommunicationFailureActive => throw new NotSupportedException();

        // onExpired is the application's code on the actor loop: it goes through the dispatcher like
        // every other callback, so its own writes are not reported back and its failure is reported
        // by the node.
        public IDeadline Arm(TimeSpan delay, Action onExpired)
        {
            if (onExpired is null) throw new ArgumentNullException(nameof(onExpired));
            return _node._deadlines.Arm(delay, () => _node.InvokeApplication(Slot, _ => onExpired()));
        }

        public void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType) => throw new NotSupportedException();

        public void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries)
        {
            if (entries is null) throw new ArgumentNullException(nameof(entries));
            if (!_node._actor.IsOnCurrentActor)
            {
                throw new InvalidOperationException(
                    "RequestTransmission runs on the node's actor loop: call it from a callback of the application.");
            }

            // All of them before any: a list with one foreign entry transmits nothing.
            var dirty = new HashSet<uint>();
            foreach (var entry in entries)
            {
                if (!ReferenceEquals(_node.ClaimantOf(entry.Index)?.Host, this))
                {
                    throw new ArgumentException(
                        $"Entry {entry.Index:X4}:{entry.Subindex:X2} is not claimed by the application that asked for its transmission.",
                        nameof(entries));
                }
                dirty.Add(CosKey(entry.Index, entry.Subindex));
            }
            if (dirty.Count == 0) return;

            // Both kinds, whatever EnableChangeOfStateTpdo and EnableChangeOfStateSrdo say: those
            // switch the automatism off, and this is a request, not the automatism.
            _node.TriggerChangeOfState(dirty);
        }

        public void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default)
            => throw new NotSupportedException();

        public void ClearError(ushort errorCode) => throw new NotSupportedException();

        public void RequestNmtState(NmtState target) => throw new NotSupportedException();
    }
}
