using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Emcy;
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

        // Read once with the claims, for the same reason.
        internal bool MonitorsCommunicationFailure { get; init; }

        // What this application was last told about the communication failure. Per application,
        // not per node: a callback of one application can change the condition before the next
        // one's turn, and each is told what is true when its turn comes. Actor loop only.
        internal bool FailureTold { get; set; }
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
                MonitorsCommunicationFailure = application.Requirements.CommunicationFailureMonitoring,
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

    // =========================================================================================
    // NMT state and reset.
    // =========================================================================================

    // The state changes not yet told to the applications, oldest first, and whether a round of
    // telling is running. Actor-confined. A callback may ask for a state (RequestNmtState), which
    // makes the node change state while it is still telling the applications about the last change:
    // the new change is queued behind it rather than delivered inside it, so each application sees
    // the changes in the order they happened, and the stack stays as deep as one callback however
    // many an application asks for. Nothing bounds an application that asks for a different state
    // on every notification, forever; that loops as any callback that never returns does.
    private readonly Queue<(NmtState Previous, NmtState Current)> _nmtNotifications = new();
    private bool _nmtNotifying;

    /// <summary>
    /// Tells every application that the node's NMT state changed from <paramref name="previous"/>
    /// to the state it is in now: a no-op when it did not change, or when no application is
    /// attached. Actor loop, after the change is made and outside <c>_od.Transaction</c>, in
    /// attach order. The state reached is read here, not when the application is called.
    /// </summary>
    private void NotifyNmtStateChanged(NmtState previous)
    {
        var current = _state;
        if (_applications.Length == 0 || previous == current) return;

        _nmtNotifications.Enqueue((previous, current));
        // A callback of the round that is running changed the state again: that round delivers it.
        if (_nmtNotifying) return;

        _nmtNotifying = true;
        try
        {
            while (_nmtNotifications.Count > 0)
            {
                var (from, to) = _nmtNotifications.Dequeue();
                foreach (var slot in _applications)
                {
                    // One that disposed the node ends the round: the rest are not called on a node
                    // that is gone.
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        _nmtNotifications.Clear();
                        return;
                    }
                    InvokeApplication(slot, application => application.OnNmtStateChanged(from, to));
                }
            }
        }
        finally
        {
            _nmtNotifying = false;
        }
    }

    /// <summary>
    /// Tells every application that the node is being reset, in attach order. Actor loop, in
    /// Initialisation, after the restore of the objects and immediately before
    /// <see cref="ICanOpenNode.ApplicationReset"/>: the application's own handler then finds the
    /// profile already in its power-on state.
    /// </summary>
    private void NotifyApplicationsOfReset(NmtCommand command)
    {
        foreach (var slot in _applications)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            InvokeApplication(slot, application => application.OnReset(command));
        }
    }

    // =========================================================================================
    // Errors of a profile, and the communication failure.
    // =========================================================================================

    // The errors the applications reported and have not cleared, with the register bits each one
    // set. They are the node's part of 1001h (NodeErrorBits), not the application's: the
    // application's part stays what SendEmcyAsync and a write to 1001h made it. Actor loop only.
    private readonly Dictionary<(ApplicationSlot Slot, ushort Code), byte> _profileErrors = new();

    /// <summary>The node's part of the register: the bits of the communication errors, of every
    /// profile error that is active, and bit 0 while any profile error is (§7.5.2.2: the generic
    /// error "shall be signaled at any error situation"). An EMCY carries it ORed with the
    /// application's part. Equal to <see cref="CommunicationErrorBits"/> on a node without
    /// applications.</summary>
    private byte NodeErrorBits
    {
        get
        {
            byte bits = CommunicationErrorBits;
            if (_profileErrors.Count == 0) return bits;
            bits |= GenericErrorBit;
            foreach (var profile in _profileErrors.Values) bits |= profile;
            return bits;
        }
    }

    /// <summary>Records an error of <paramref name="slot"/>'s application and orders its EMCY: once per
    /// error event, so an error that is active already does nothing (CiA 301 §7.2.7.1). Unlike a
    /// communication error it is recorded while the EMCY is switched off too, since the register has
    /// to show it; the EMCY is then written to the register without a frame, as in Stopped it is held.</summary>
    private void RaiseProfileError(ApplicationSlot slot, ushort errorCode, byte errorRegisterBits,
        ReadOnlySpan<byte> manufacturerSpecific)
    {
        if (errorCode == ErrorResetCode)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode,
                "0000h is the error reset: ClearError sends it.");
        }
        if (manufacturerSpecific.Length > EmcyMessage.ManufacturerFieldLength)
        {
            throw new ArgumentException(
                $"The manufacturer-specific field is at most {EmcyMessage.ManufacturerFieldLength} bytes.",
                nameof(manufacturerSpecific));
        }
        RequireActor(nameof(ApplicationHost.RaiseError));

        var key = (slot, errorCode);
        if (_profileErrors.ContainsKey(key)) return;
        _profileErrors[key] = errorRegisterBits;
        EmitNodeEmcy(errorCode, manufacturerSpecific.ToArray());
    }

    /// <summary>Ends an error of <paramref name="slot"/>'s application: nothing when it is not active,
    /// else one <c>0000h</c> that carries what the register still holds (§7.2.7.1: an error reset
    /// "may be transmitted together with the remaining errors").</summary>
    private void ClearProfileError(ApplicationSlot slot, ushort errorCode)
    {
        RequireActor(nameof(ApplicationHost.ClearError));
        if (!_profileErrors.Remove((slot, errorCode))) return;
        EmitErrorReset();
    }

    /// <summary>NMT reset: the profile errors end with the communication errors, without an EMCY. A
    /// profile whose cause persists raises its error again from <c>OnReset</c>.</summary>
    private void EndProfileErrorsForReset() => _profileErrors.Clear();

    private void RequireActor(string member)
    {
        if (_actor.IsOnCurrentActor) return;
        throw new InvalidOperationException(
            $"{member} runs on the node's actor loop: call it from a callback of the application.");
    }

    /// <summary>Whether a communication failure is active: the condition error behaviour acts on
    /// (<c>1029h</c>, <see cref="ResolveErrorBehaviorTarget"/>). Actor loop.</summary>
    private bool CommunicationFailure
        => _controllerObservedBusOff || _heartbeatEmcyTimeouts.Count > 0 || _lifeGuardingOccurred;

    // Whether PublishCommunicationFailure is telling the applications right now, and whether an NMT
    // reset holds the telling back until its clearing is done. Actor loop only.
    private bool _publishingFailure;
    private bool _failurePublishDeferred;

    /// <summary>
    /// Tells the applications that asked for it that the communication failure began or ended, each
    /// one when what it was last told differs from the condition. Called after every mutation of the
    /// operands of <see cref="CommunicationFailure"/>, and before whatever reacts to the change
    /// (<see cref="ApplyCommunicationErrorBehavior"/>), so a profile sets its outputs to the fault
    /// value before the node leaves Operational. Actor loop.
    /// </summary>
    /// <remarks>
    /// An application's callback can change the operands (it owns the dictionary: a write to
    /// <c>1016h</c> or <c>100Dh</c> ends a watch), which calls this again from inside the round. That
    /// call returns at once; the round that is running re-reads the condition before every
    /// application and goes round again until nobody is behind. So every application ends on the
    /// truth and is never told a state that was withdrawn before its turn. The failure is a level,
    /// not an event: unlike the NMT changes it is not queued for everyone in order, as an
    /// application that was told <see langword="true"/> after the failure was over would drive its
    /// outputs to the fault value for nothing.
    /// </remarks>
    private void PublishCommunicationFailure()
    {
        if (_applications.Length == 0 || _publishingFailure || _failurePublishDeferred) return;

        _publishingFailure = true;
        try
        {
            bool told;
            do
            {
                told = false;
                foreach (var slot in _applications)
                {
                    if (!slot.MonitorsCommunicationFailure) continue;
                    if (Volatile.Read(ref _disposed) != 0) return;
                    bool active = CommunicationFailure;
                    if (slot.FailureTold == active) continue;
                    slot.FailureTold = active;
                    told = true;
                    InvokeApplication(slot, application => application.OnCommunicationFailureChanged(active));
                }
            }
            while (told);
        }
        finally
        {
            _publishingFailure = false;
        }
    }

    /// <summary>An NMT reset ends the communication failure as part of its clearing. Nothing is
    /// published until the clearing is done (<see cref="ResumeFailurePublishAfterReset"/>): an
    /// application called between two steps of it would raise an error that the next step then
    /// wipes from <c>1001h</c> while the node still holds it as active.</summary>
    private void DeferFailurePublishForReset() => _failurePublishDeferred = true;

    /// <summary>The reset's clearing is done: tells the applications what became of the failure, in
    /// Initialisation, immediately before <see cref="NotifyApplicationsOfReset"/>.</summary>
    private void ResumeFailurePublishAfterReset()
    {
        _failurePublishDeferred = false;
        PublishCommunicationFailure();
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

        public bool CommunicationFailureActive => _node.CommunicationFailure;

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
            => _node.RaiseProfileError(Slot, errorCode, errorRegisterBits, manufacturerSpecific);

        public void ClearError(ushort errorCode) => _node.ClearProfileError(Slot, errorCode);

        // Leaving Operational, or a state a profile's own fault calls for. Never Operational: only
        // the master's NMT Start puts the node there.
        public void RequestNmtState(NmtState target)
        {
            if (target is not (NmtState.PreOperational or NmtState.Stopped))
            {
                throw new ArgumentOutOfRangeException(nameof(target), target,
                    "An application may ask for Pre-Operational or Stopped: it does not start its own node.");
            }
            if (!_node._actor.IsOnCurrentActor)
            {
                throw new InvalidOperationException(
                    "RequestNmtState runs on the node's actor loop: call it from a callback of the application.");
            }

            // Initializing is the node's own: a reset or a boot that has not finished, which ends in
            // Pre-Operational by itself.
            var state = _node._state;
            if (state == target || state == NmtState.Initializing) return;

            // Tells every application about the change, this one included.
            _node.ApplyNmtTransition(target);
        }
    }
}
