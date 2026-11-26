using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
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

    // The highest index an application may claim, the last of the standardized system variable area:
    // C000h to FFFFh are reserved (CiA 301 v4.2.0 §7.4.1, Table 41), #555.
    private const ushort LastClaimableIndex = 0xBFFF;

    // Bit 6 of 1001h: reserved, always 0b (CiA 301 v4.2.0 §7.5.2.2, Table 54). A profile may not set it.
    private const byte ReservedErrorRegisterBit = 0x40;

    // One per attached application, in attach order. Written once, on the constructing thread,
    // before the node's actor runs anything; empty for a node opened without applications.
    private ApplicationSlot[] _applications = Array.Empty<ApplicationSlot>();

    private HashSet<uint>? _operationalTransmissionEntries;

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

        // The highest PDO number the application configures (CanOpenApplicationRequirements.PdoCount),
        // read once with the claims: the bound of its default PDOs.
        internal int PdoCount { get; init; }

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
    // The requirements of each listed application, read once, before the dictionary is built
    // (ReadApplicationRequirements), index by index with CanOpenNodeOptions.Applications; null for a
    // node without applications. Constructing thread only.
    private CanOpenApplicationRequirements?[]? _applicationRequirements;

    /// <summary>
    /// Reads the requirements of <see cref="CanOpenNodeOptions.Applications"/>, once, before the
    /// communication profile is populated: the error behaviour layout one of them asks for shapes
    /// <c>1029h</c> as it is declared and as a device description is loaded against it
    /// (<see cref="TakeErrorBehaviorLayout"/>). An entry that is null or listed a second time is
    /// skipped here and refused by <see cref="AttachApplications"/>, which takes the requirements
    /// read here. Nothing on a node without applications.
    /// </summary>
    private void ReadApplicationRequirements()
    {
        var listed = _options.Applications;
        if (listed.Count == 0) return;
        var read = new CanOpenApplicationRequirements?[listed.Count];
        for (int i = 0; i < read.Length; i++)
        {
            if (listed[i] is not { } application) continue;
            bool listedBefore = false;
            for (int earlier = 0; earlier < i; earlier++) listedBefore |= ReferenceEquals(listed[earlier], application);
            if (listedBefore) continue;
            var requirements = application.Requirements;
            read[i] = requirements;
            if (requirements?.ErrorBehavior is { } layout) TakeErrorBehaviorLayout(application, layout);
        }
        _applicationRequirements = read;
    }

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
            // Read before the dictionary was built (ReadApplicationRequirements), not again here.
            var requirements = _applicationRequirements![i]!;
            slots[i] = new ApplicationSlot(application, host)
            {
                Claims = ValidatedClaims(application, slots, i),
                MonitorsCommunicationFailure = requirements.CommunicationFailureMonitoring,
                PdoCount = requirements.PdoCount,
            };
            host.Slot = slots[i];
            ValidateRequirements(application, requirements);
        }

        _applications = slots;
        var commandEntries = new HashSet<uint>();
        foreach (var slot in slots)
        {
            _attachingSlot = slot;
            try
            {
                slot.Application.Attach(slot.Host);
            }
            finally
            {
                _attachingSlot = null;
            }
            // Read once, after Attach: the list may name what the application declared there.
            commandEntries.UnionWith(ValidatedCommandEntries(slot));
            if (slot.Application.OperationalTransmissionEntries is { } entries)
            {
                _operationalTransmissionEntries ??= new HashSet<uint>();
                foreach (var entry in entries)
                {
                    if (!ReferenceEquals(ClaimantOf(entry.Index), slot))
                        throw OptionsError($"Application '{slot.Application.Name}' requests startup transmission of unclaimed entry {entry.Index:X4}:{entry.Subindex:X2}.");
                    _operationalTransmissionEntries.Add(CosKey(entry.Index, entry.Subindex));
                }
            }
        }
        if (commandEntries.Count > 0) _commandEntries = commandEntries;
        ComposeDeviceTypes();
        ComposeDefaultPdos();

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
            if (range.Last > LastClaimableIndex)
            {
                throw OptionsError($"Application '{application.Name}' claims {range}, which reaches above 0x{LastClaimableIndex:X4}: " +
                    "C000h to FFFFh are reserved (CiA 301 v4.2.0 §7.4.1, Table 41).");
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

    private void ValidateRequirements(ICanOpenApplication application, CanOpenApplicationRequirements requirements)
    {
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
        if (requirements.ErrorBehavior is not null && !_options.EnableErrorBehavior)
        {
            throw OptionsError($"Application '{application.Name}' lays out the error behaviour record 1029h, which the node " +
                "has only with error behaviour: set CanOpenNodeOptions.EnableErrorBehavior to true.");
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
    /// <see cref="ObjectDictionary.EntryStored"/>, ahead of every subscriber of
    /// <see cref="ObjectDictionary.EntryWritten"/>, on the writing thread and under the dictionary's
    /// write gate: a stored write to
    /// an entry an application claims. Records the entry, or for a command entry whose write was
    /// validated (<see cref="ReserveCommandWrite"/>) queues a copy of the value it stored, and queues
    /// one evaluation, which tells the claimant on the actor loop
    /// (<see cref="EvaluateApplicationEntriesOnActor"/>). Not for what the claimant writes from
    /// inside its own callback, and not for a restore of values in a reset: neither is news to it.
    /// </summary>
    private void OnOdEntryWrittenForApplications(ushort index, byte subindex, CanOpenWriteSource source)
    {
        if (_applications.Length == 0) return;
        // Taken by this write or by none: a reservation belongs to the write it was made for, which
        // is the one being reported here when the keys match (ReserveCommandWrite).
        var reserved = _reservedCommand;
        _reservedCommand = null;
        // RestoreValues sets this under the write gate, which this write holds too: it is true for
        // the restore's own writes and for nobody else's.
        if (_restoringValues) return;
        var claimant = ClaimantOf(index);
        if (claimant is null) return;
        // The claimant's own write from inside one of its callbacks. IsOnCurrentActor first: the
        // field belongs to the actor loop, and a write from another thread is never that.
        if (_actor.IsOnCurrentActor && ReferenceEquals(_applicationInCallback, claimant)) return;
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = CosKey(index, subindex);
        // Under the write gate, before anyone else can write the entry: the value this write stored.
        // An entry that is gone again has no value to tell; it is reported as written, as it would
        // be without a reservation.
        byte[]? command = reserved == key && _od.TryReadRaw(index, subindex, out var stored) ? stored : null;
        bool post;
        lock (_applicationGate)
        {
            if (command is not null)
            {
                if (source.AnsweringRpdo is { } rpdo && !ReferenceEquals(PdoOwnerOf(rpdo), claimant))
                    source = source with { AnsweringRpdo = null };
                (_queuedCommands ??= new()).Add(new(claimant, new(index, subindex), command, _commandWriteGeneration, source));
            }
            else (_applicationDirty ??= new HashSet<uint>()).Add(key);
            post = !_applicationEvaluationPosted;
            _applicationEvaluationPosted = true;
        }
        if (post) PostApplicationEvaluation();
    }

    /// <summary>Queues the one evaluation of the application round, once the flag that says it is
    /// queued has been set under <c>_applicationGate</c>; an actor that has ended takes nothing, and
    /// the flag is cleared again.</summary>
    private void PostApplicationEvaluation()
    {
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
    /// Tells each claimant its command writes, in commit order (<see cref="DeliverCommandWrites"/>),
    /// then the entries written since it was last told: sorted by index, then sub-index, each once;
    /// then the RPDOs taken since (<see cref="DeliverReceptions"/>). Actor loop. Writes landing while this runs re-arm the queue and the dirty set and queue the
    /// next evaluation, so none is lost; one claimant that throws does not keep the next from being
    /// told (<see cref="InvokeApplication"/>), one that disposes the node does.
    /// </summary>
    private void EvaluateApplicationEntriesOnActor()
    {
        HashSet<uint>? dirty;
        List<QueuedCommand>? commands;
        List<(ApplicationSlot Slot, int PdoNumber)>? receptions;
        lock (_applicationGate)
        {
            dirty = _applicationDirty;
            _applicationDirty = null;
            commands = _queuedCommands;
            _queuedCommands = null;
            receptions = _queuedReceptions;
            _queuedReceptions = null;
            _applicationEvaluationPosted = false;
        }
        if (Volatile.Read(ref _disposed) != 0) return;
        if (commands is not null && !DeliverCommandWrites(commands)) return;
        if (dirty is { Count: > 0 } && !DeliverWrittenEntries(dirty)) return;
        // Last: an application hears of an RPDO once it has been told what the RPDO wrote.
        if (receptions is not null) DeliverReceptions(receptions);
    }

    /// <summary>Tells each claimant its entries of <paramref name="dirty"/>, sorted and each once.
    /// Returns <see langword="false"/> when a callback disposed the node, which ends the round.</summary>
    private bool DeliverWrittenEntries(HashSet<uint> dirty)
    {
        var keys = new uint[dirty.Count];
        dirty.CopyTo(keys);
        Array.Sort(keys); // CosKey is index << 8 | sub-index: its order is index, then sub-index

        foreach (var slot in _applications)
        {
            // One that disposed the node ends the round: the rest are not called on a node that is
            // gone, as in the other rounds (NotifyNmtStateChanged, PublishCommunicationFailure).
            if (Volatile.Read(ref _disposed) != 0) return false;
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
        return Volatile.Read(ref _disposed) == 0;
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
        // ValidateWrite is not an actor-loop callback of the application, even when the write it
        // decides is made from inside one: the innermost callback is then this one, under the
        // write gate, and a Post made from it is queued, not run there (ICanOpenApplicationHost.Post).
        // Restored on the way out, before the write is stored and reported.
        bool onActor = _actor.IsOnCurrentActor;
        var outer = onActor ? _applicationInCallback : null;
        if (onActor) _applicationInCallback = null;
        // A reservation left by a validated write that was not stored after all goes with the next
        // validation (ReserveCommandWrite).
        _reservedCommand = null;
        OdWriteDecision decision;
        try
        {
            decision = slot.Application.ValidateWrite(index, subindex, value);
        }
        // Everything, deliberately: this is the application's code, and whatever it throws must not
        // reach the dictionary's writer, which on an SDO download or a PDO is the node's own actor
        // loop. It goes to BackgroundExceptionOccurred, and the write is refused.
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
            return OdWriteDecision.Reject(SdoAbortCode.General);
        }
        finally
        {
            if (onActor) _applicationInCallback = outer;
        }
        // The queue of command writes is checked once the application has accepted the write, so a
        // value it refuses is refused with its code whatever the queue holds. Whose write it is goes
        // by the callback that was running before the validation, as for OnEntriesWritten.
        if (!decision.Store || decision.Abort is not null) return decision;
        return ReserveCommandWrite(index, subindex, own: onActor && ReferenceEquals(outer, slot));
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
        // Everything, deliberately: this is the application's code running on the node's actor loop,
        // and nothing it throws may end that loop or keep the next application from its turn. It
        // goes to BackgroundExceptionOccurred.
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
    // Work an application posts to the actor loop (ICanOpenApplicationHost.Post).
    // =========================================================================================

    // The posts made, and the deadline callbacks that came due (RunApplicationDeadline), before the
    // node has opened, oldest first: the actor's thread runs from early in the constructor, and a post
    // made in Attach, or by another thread once Attach has handed the application its host, would
    // otherwise run while the constructor still composes the default PDOs and takes the power-on
    // values. Non-null while held; released behind the start-up job
    // (ReleaseApplicationPosts), dropped when the open fails (CloseApplicationPosts). Under
    // _applicationPostGate, as is _applicationPostsClosed.
    private readonly object _applicationPostGate = new();
    private List<Action>? _heldApplicationPosts = new();
    private bool _applicationPostsClosed;

    /// <summary>
    /// <see cref="ICanOpenApplicationHost.Post"/>: runs <paramref name="action"/> at once when the
    /// actor loop is inside one of <paramref name="slot"/>'s callbacks, and otherwise queues it as a
    /// callback of that application — held until the node has opened, dropped once it is disposed
    /// or its open failed.
    /// </summary>
    private void PostApplicationWork(ApplicationSlot slot, Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        // IsOnCurrentActor first: the field belongs to the actor loop, as in
        // OnOdEntryWrittenForApplications.
        if (_actor.IsOnCurrentActor && ReferenceEquals(_applicationInCallback, slot))
        {
            action();
            return;
        }
        if (Volatile.Read(ref _disposed) != 0) return;

        Action work = () =>
        {
            // Queued before the disposal and not started when it came: the node is gone.
            if (Volatile.Read(ref _disposed) != 0) return;
            InvokeApplication(slot, _ => action());
        };
        lock (_applicationPostGate)
        {
            if (_applicationPostsClosed) return;
            if (_heldApplicationPosts is { } held)
            {
                held.Add(work);
                return;
            }
        }
        try
        {
            _actor.Post(work);
        }
        catch (ObjectDisposedException)
        {
            // The actor ended between the check above and the post: dropped, as documented.
        }
    }

    /// <summary>The node has ordered its start-up: the posts held until now are queued behind it, in
    /// the order they were made, and later ones go straight to the actor. Constructing thread, once.</summary>
    private void ReleaseApplicationPosts()
    {
        lock (_applicationPostGate)
        {
            var held = _heldApplicationPosts;
            _heldApplicationPosts = null;
            // Under the gate: a post that waits for it lands behind these, never between them.
            if (held is not null)
            {
                foreach (var work in held) _actor.Post(work);
            }
        }
    }

    // Whether the start-up job has begun. Actor loop only: set by that job, read by a deadline callback.
    private bool _startUpBegun;

    /// <summary>
    /// A deadline of <paramref name="slot"/>'s application came due (<see cref="ICanOpenApplicationHost.Arm"/>).
    /// Actor loop. Before the node has opened its callback is held like a post (#561): the actor's
    /// thread runs from early in the constructor, and a deadline armed in <c>Attach</c> and due at once
    /// would otherwise run while the constructor still attaches the next application, composes the
    /// device types and the default PDOs and takes the power-on values. Held, it is queued behind the
    /// start-up job with the posts, in the order the deadlines came due, and dropped when the open
    /// fails. After the release and before the start-up job has run, it is queued behind that job;
    /// from then on it runs at once.
    /// </summary>
    /// <remarks>The deadline itself is armed when <c>Arm</c> is called and expires when it comes due,
    /// so its time counts from the call and <see cref="IDeadline.IsExpired"/> is true while the
    /// callback is held; a <c>Dispose</c> then is too late to stop it, as for any expired deadline.</remarks>
    private void RunApplicationDeadline(ApplicationSlot slot, Action onExpired)
    {
        // Built once, outside work: nested in it, the lambda would get a lazily cached delegate whose
        // second use never comes, as each callback runs once.
        Action<ICanOpenApplication> callback = _ => onExpired();
        Action work = () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            InvokeApplication(slot, callback);
        };
        lock (_applicationPostGate)
        {
            if (_applicationPostsClosed) return;
            if (_heldApplicationPosts is { } held)
            {
                held.Add(work);
                return;
            }
        }
        if (!_startUpBegun)
        {
            // Released, and the start-up job is in the mailbox ahead of this. As for a Post: a node
            // disposed meanwhile drops it, and so does an actor that ended between the check and the
            // post, without a throw (the actor may be shutting down and draining its due timers).
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                _actor.Post(work);
            }
            catch (ObjectDisposedException)
            {
                // Dropped, as documented.
            }
            return;
        }
        work();
    }

    /// <summary>The open failed: what is held is dropped, and so is every later post.</summary>
    private void CloseApplicationPosts()
    {
        lock (_applicationPostGate)
        {
            _heldApplicationPosts = null;
            _applicationPostsClosed = true;
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
                if (to == NmtState.Operational && _operationalTransmissionEntries is { } entries)
                    TriggerChangeOfState(entries, includeAllProfileTpdos: true);
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
            // One that disposed the node ends the round, as in the others.
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
    private byte NodeErrorBits => (byte)(CommunicationErrorBits | ProfileErrorBits);

    /// <summary>The bits of every profile error that is active, and bit 0 while any is: 0 on a node
    /// without applications. Unlike the communication errors, which <c>1001h</c> shows from the EMCY
    /// that reports them, these are what <c>1001h</c> shows at every moment: every write of the
    /// register takes them as they are then (<see cref="UpdateErrorRegister"/>), and a raise or a
    /// clear writes it at once (<see cref="ShowProfileErrorsInRegister"/>), so neither an EMCY waiting
    /// for its inhibit time nor an older one transmitted later hides or brings back a profile error
    /// (#550).</summary>
    private byte ProfileErrorBits
    {
        get
        {
            if (_profileErrors.Count == 0) return 0;
            byte bits = GenericErrorBit;
            foreach (var profile in _profileErrors.Values) bits |= profile;
            return bits;
        }
    }

    /// <summary>A profile error began or ended: <c>1001h</c> shows it now (CiA 301 v4.2.0 §7.5.2.2:
    /// "If a specific error occurs the corresponding bit shall be set"), not when its EMCY gets its
    /// turn on the chain. The communication bits stay those of the last register write on the
    /// chain, and the application's part is taken as at any step (the notice of its own writes,
    /// #417). Actor loop.</summary>
    private void ShowProfileErrorsInRegister()
    {
        // From here on a reset writes the register it leaves behind (EndCommunicationErrorsForReset).
        _errorRegisterQueued = true;
        UpdateErrorRegister(passed: null, _registerCommunicationBits);
    }

    // The EMCYs of profile errors raised in Initialisation, and the warnings sent there, oldest
    // first, not yet ordered: in that state only the boot-up object exists (CiA 301 §7.3.2.2.1,
    // Table 37), so they wait for it (ReleaseProfileEmcysAfterBootUp). Its errors are exactly the
    // errors active in Initialisation, as every one of those was raised in it: a reset ends all
    // profile errors and drops this list before it enters the state, and whatever calls a profile
    // before the start-up job does so in the start-up's Initialisation.
    // So an error cleared before the boot-up leaves its entry and nothing else; a warning is not an
    // error, and no clear takes it out (#556). The errors are bounded by those active at once; the
    // warnings by what a profile sends while an LSS slave waits for its node-id. Null when empty.
    // The node's own communication errors raised there are held in the same list, in their order
    // among the profile's: Slot null, Communication their key (RaiseCommunicationError, #547).
    // Actor loop only.
    private List<(ApplicationSlot? Slot, ushort Code, byte[] ManufacturerSpecific, bool Warning, int? Communication)>? _heldEmcys;

    /// <summary>Records an error of <paramref name="slot"/>'s application and orders its EMCY: once per
    /// error event, so an error that is active already does nothing (CiA 301 §7.2.7.1). Unlike a
    /// communication error it is recorded while the EMCY is switched off too, since the register has
    /// to show it; the EMCY is then written to the register without a frame, as in Stopped it is held.
    /// In Initialisation the EMCY is held until the boot-up has been ordered.</summary>
    private void RaiseProfileError(ApplicationSlot slot, ushort errorCode, byte errorRegisterBits,
        ReadOnlySpan<byte> manufacturerSpecific)
    {
        if (errorCode == ErrorResetCode)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode,
                "0000h is the error reset: ClearError sends it.");
        }
        if ((errorRegisterBits & ReservedErrorRegisterBit) != 0)
        {
            // Refused rather than masked: a profile that sets it has a bug, and dropping the bit would
            // record an error the profile did not describe (#554).
            throw new ArgumentOutOfRangeException(nameof(errorRegisterBits), errorRegisterBits,
                "Bit 6 of the error register is reserved and always 0 (CiA 301 v4.2.0 §7.5.2.2, Table 54).");
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
        ShowProfileErrorsInRegister();
        var manufacturer = manufacturerSpecific.ToArray();
        if (_state == NmtState.Initializing)
        {
            (_heldEmcys ??= new()).Add((slot, errorCode, manufacturer, Warning: false, Communication: null));
            return;
        }
        if (_state != NmtState.Stopped) EmcyChainWaitsForBootUp();
        EmitNodeEmcy(errorCode, manufacturer, profile: true);
    }

    /// <summary>Ends an error of <paramref name="slot"/>'s application: nothing when it is not active,
    /// else one <c>0000h</c> that carries what the register still holds (§7.2.7.1: an error reset
    /// "may be transmitted together with the remaining errors"). In Initialisation the error's EMCY
    /// is still held, so there is nothing on the bus to reset: the held EMCY is dropped instead,
    /// and nothing is sent.</summary>
    private void ClearProfileError(ApplicationSlot slot, ushort errorCode)
    {
        RequireActor(nameof(ApplicationHost.ClearError));
        if (!_profileErrors.Remove((slot, errorCode))) return;
        ShowProfileErrorsInRegister();
        if (_state == NmtState.Initializing)
        {
            _heldEmcys?.RemoveAll(held => !held.Warning && ReferenceEquals(held.Slot, slot) && held.Code == errorCode);
            return;
        }
        if (_state != NmtState.Stopped) EmcyChainWaitsForBootUp();
        EmitNodeEmcy(ErrorResetCode, profile: true);
    }

    /// <summary>The highest error code of the class "error reset or no error", <c>00xxh</c> (CiA 301
    /// v4.2.0 §7.2.7.1, Table 25): the codes of a warning are <c>0001h</c> to this.</summary>
    private const ushort HighestNoErrorCode = 0x00FF;

    /// <summary>Sends a warning of <paramref name="slot"/>'s application: one EMCY of the class
    /// <c>00xxh</c> that is no active error and writes nothing to <c>1001h</c> (see
    /// <see cref="ICanOpenApplicationHost.SendWarning"/>). In Initialisation it is held with the
    /// errors' EMCYs until the boot-up has been ordered; in Stopped it is dropped, as Stopped keeps
    /// the most recent active EMCY reason only (CiA 301 v4.2.0 §7.3.2.2.4); otherwise it takes the
    /// gated path of a profile EMCY, behind a pending boot-up.</summary>
    private void SendProfileWarning(ApplicationSlot slot, ushort errorCode, ReadOnlySpan<byte> manufacturerSpecific)
    {
        if (errorCode is ErrorResetCode or > HighestNoErrorCode)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode,
                "A warning is of the class 00xxh, \"error reset or no error\" (CiA 301 v4.2.0 §7.2.7.1, Table 25), "
                + "0001h to 00FFh: an error of another class is raised with RaiseError, and 0000h is the error reset.");
        }
        if (manufacturerSpecific.Length > EmcyMessage.ManufacturerFieldLength)
        {
            throw new ArgumentException(
                $"The manufacturer-specific field is at most {EmcyMessage.ManufacturerFieldLength} bytes.",
                nameof(manufacturerSpecific));
        }
        RequireActor(nameof(ApplicationHost.SendWarning));

        var manufacturer = manufacturerSpecific.ToArray();
        switch (_state)
        {
            case NmtState.Initializing:
                (_heldEmcys ??= new()).Add((slot, errorCode, manufacturer, Warning: true, Communication: null));
                return;
            case NmtState.Stopped:
                return;
            default:
                EmcyChainWaitsForBootUp();
                _ = EmitEmcy(new EmcyMessage(_nodeId, errorCode, 0, manufacturer), gate: true, warning: true);
                return;
        }
    }

    // Counts the NMT resets that ended the profile errors. A profile EMCY released behind a boot-up
    // carries the count of its release, and its step on the EMCY chain sends and writes nothing when
    // the count has moved on (ReleaseProfileEmcysAfterBootUp, EmitEmcy). Actor loop only.
    private int _profileResetGeneration;

    /// <summary>NMT reset: the profile errors end with the communication errors, without an EMCY, and
    /// so do the EMCYs of profile errors raised in an earlier Initialisation that have not gone out:
    /// one still held is dropped from the list, and one that <see cref="ReleaseProfileEmcysAfterBootUp"/>
    /// has already put on the EMCY chain behind a boot-up, but not yet transmitted, finds the reset
    /// generation changed when its turn comes and sends and writes nothing. A profile whose cause
    /// persists raises its error again from <c>OnReset</c>; in Initialisation, its EMCY then follows
    /// the boot-up.</summary>
    private void EndProfileErrorsForReset()
    {
        _profileErrors.Clear();
        // The node's communication errors held there end with this reset too (EndCommunicationErrorsForReset).
        _heldEmcys = null;
        _profileResetGeneration++;
    }

    /// <summary>
    /// The boot-up (<paramref name="bootUp"/>) is ordered and the node has left Initialisation: the
    /// profile EMCYs held there, and those of the node's own communication errors (#547), go out
    /// behind it, in the order they were raised, each with the register of the moment it is
    /// released, as a held EMCY in Stopped does. A no-op when nothing is held. Actor loop.
    /// </summary>
    /// <remarks>
    /// The boot-up travels on the heartbeat chain and the EMCY on its own, so ordering one before the
    /// other on the actor does not order them on the bus. The EMCY chain therefore waits for the
    /// boot-up's send first: that task completes once the driver has confirmed the frame
    /// (SendControlFrame), or once it was dropped, failed or cancelled, which ends the wait too.
    /// <para>
    /// A master can send an NMT reset as soon as it sees the boot-up, before the released EMCYs have
    /// had their turn. That reset ends their errors (<see cref="EndProfileErrorsForReset"/>), so each
    /// released EMCY carries the reset generation of its release, and is decided on the actor in the
    /// turn that would transmit it, after the wait: a reset handled before that turn drops it, frame
    /// and <c>1001h</c> write alike.
    /// </para>
    /// <para>
    /// A master can as well stop the node before that turn. The released EMCYs therefore take the
    /// gated path of the EMCY chain whatever the options (<see cref="EmitEmcy"/>): in Stopped the
    /// step waits until the node leaves it (CiA 301 v4.2.0 §7.3.2.2.4, Table 37), and a reset ends the
    /// wait without a frame (#549).
    /// </para>
    /// </remarks>
    private void ReleaseProfileEmcysAfterBootUp(Task bootUp)
    {
        _bootUpSend = bootUp;
        var held = _heldEmcys;
        if (held is not { Count: > 0 }) return;
        _heldEmcys = null;
        AppendBootUpWait(bootUp);
        int generation = _profileResetGeneration;
        foreach (var (_, code, manufacturer, warning, communication) in held)
        {
            // Its 0000h will follow it on the gated path (EmitErrorReset).
            if (communication is not null) _communicationErrorReleased = true;
            _ = EmitEmcy(new EmcyMessage(_nodeId, code, 0, manufacturer), profileResetGeneration: generation, warning: warning);
        }
    }

    // The send of the boot-up the node ordered last (ReleaseProfileEmcysAfterBootUp). Actor loop only.
    private Task _bootUpSend = Task.CompletedTask;

    /// <summary>A profile EMCY ordered in Pre-Operational or Operational while the boot-up's send is
    /// still pending waits for it on the EMCY chain, as the EMCYs held in Initialisation do: the
    /// work of a profile that was held behind the start-up job (a post or a deadline from
    /// <c>Attach</c>, #561) runs right after the boot-up has been ordered, and its EMCY must not
    /// overtake the boot-up on the bus. Nothing once the boot-up is out. Like every profile EMCY it
    /// takes the gated path (<see cref="EmitEmcy"/>), so a Stop or a reset during the wait is
    /// honoured (#549). Actor loop.</summary>
    private void EmcyChainWaitsForBootUp()
    {
        if (!_bootUpSend.IsCompleted) AppendBootUpWait(_bootUpSend);
    }

    /// <summary>Puts a wait for <paramref name="bootUp"/>'s send on the EMCY chain. Actor loop.</summary>
    private void AppendBootUpWait(Task bootUp)
        => _ = AppendToEmcyChain(() =>
        {
            HeldEmcysWaitForBootUpForTests?.Invoke();
            return bootUp.ContinueWith(static sent => { _ = sent.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });

    /// <summary>Test seam: runs on the actor when the EMCY chain starts waiting for the boot-up
    /// before it sends a profile EMCY (<see cref="AppendBootUpWait"/>), or an EMCY that waited in
    /// Stopped (<see cref="SendWhenNotStoppedAsync"/>).</summary>
    internal Action? HeldEmcysWaitForBootUpForTests { get; set; }

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
                // MonitorsCommunicationFailure is read once, when the node opens; FailureTold below is
                // what the round changes, so that test stays in the body.
                foreach (var slot in _applications.Where(monitoring => monitoring.MonitorsCommunicationFailure))
                {
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

        public bool IsOnActor => _node._actor.IsOnCurrentActor;

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
            return _node._deadlines.Arm(delay, () => _node.RunApplicationDeadline(Slot, onExpired));
        }

        public void Post(Action action) => _node.PostApplicationWork(Slot, action);

        public void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType)
            => _node.SetApplicationDeviceType(Slot, logicalDevice, deviceType);

        public void SetDefaultTpdo(int pdoNumber, PdoMapping mapping, bool enabled = true)
            => _node.SetApplicationDefaultPdo(Slot, isTpdo: true, pdoNumber, mapping, enabled);

        public void SetDefaultRpdo(int pdoNumber, PdoMapping mapping)
            => _node.SetApplicationDefaultPdo(Slot, isTpdo: false, pdoNumber, mapping, enabled: true);

        public void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries, int? answeringRpdo = null)
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
            _node.TriggerChangeOfState(dirty, answeringRpdo: answeringRpdo);
        }

        public NmtState? ErrorBehaviorTarget(byte subindex)
        {
            if (subindex == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(subindex), subindex,
                    "1029h sub-index 00h is the highest sub-index supported, not an error class.");
            }
            if (!_node._actor.IsOnCurrentActor)
            {
                throw new InvalidOperationException(
                    "ErrorBehaviorTarget runs on the node's actor loop: call it from a callback of the application.");
            }
            return _node.ErrorBehaviorTargetOf(subindex);
        }

        public void RequestTransmissionOfTpdo(int pdoNumber)
        {
            if (!_node._actor.IsOnCurrentActor)
            {
                throw new InvalidOperationException(
                    "RequestTransmissionOfTpdo runs on the node's actor loop: call it from a callback of the application.");
            }
            _node.TransmitApplicationTpdo(Slot, pdoNumber);
        }

        public void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default)
            => _node.RaiseProfileError(Slot, errorCode, errorRegisterBits, manufacturerSpecific);

        public void ClearError(ushort errorCode) => _node.ClearProfileError(Slot, errorCode);

        public void SendWarning(ushort errorCode, ReadOnlySpan<byte> manufacturerSpecific = default)
            => _node.SendProfileWarning(Slot, errorCode, manufacturerSpecific);

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
