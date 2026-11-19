using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The status word of a CiA 408 device as <see cref="Cia408Peer.ReadStatusAsync"/> read it
/// (CiA 408 v2.2.0 §9.3.3, Table 76; §9.3.7, Table 88), decoded.</summary>
/// <param name="Word">The status word as the device sent it: 16 bits from <c>6041h</c> or 32 bits
/// from <c>6045h</c>, whichever the peer uses (see <see cref="Cia408Peer"/>).</param>
/// <param name="State">The state bits 3..0 (R M H D) encode (§7.6, Table 4, Figure 1):
/// <c>1000b</c> INIT, <c>1001b</c> DISABLED, <c>1011b</c> HOLD, <c>1111b</c> ACTIVE, <c>0000b</c>
/// FAULT_INIT, <c>0001b</c> FAULT_DISABLED, <c>0011b</c> FAULT_HOLD — and
/// <see cref="Cia408State.FaultReaction"/> for every other pattern with R = 0. See the remarks.</param>
/// <remarks>
/// <para><b>NOT_READY</b> has no encoding of its own in the norm; <c>0000b</c> is read as FAULT_INIT, a
/// resting state, since a device leaves NOT_READY on its own before it answers SDO
/// (<see cref="Cia408StateMachine.StatusBits"/>).</para>
/// <para><b>FAULT_REACTION</b> is encoded twice, and differently: Figure 1 gives it <c>0xxx</c>, while
/// Table 4 says bits 0 to 3 keep the values of the state the fault came from. The reading taken: a
/// pattern with R = 0 that is no fault state's own (<c>0010b</c>, <c>0100b</c> to <c>0111b</c>) can
/// only be FAULT_REACTION by Figure 1, and is decoded as it; a device that follows Table 4 shows the
/// state it came from, which no reader can tell apart from that state. Either way FAULT_REACTION is
/// passed through, not rested in, and the command sequences keep reading through it within their
/// bound (<see cref="Cia408PeerOptions.StatusReads"/>). A pattern with R = 1 that encodes no state
/// (<c>1010b</c>, <c>1100b</c> to <c>1110b</c>) has no reading in either passage and is
/// <see cref="InvalidDataException"/>.</para>
/// </remarks>
internal readonly record struct Cia408Status(uint Word, Cia408State State)
{
    /// <summary>Bit 4, L: local operation active, the control word comes from the device's local
    /// source and a written one is ignored (<c>604Fh</c> = 1, §9.3.9).</summary>
    public bool Local => (Word & Cia408StatusWord.Local) != 0;

    /// <summary>Bit 5, W: a warning is active; <c>604Eh</c> gives its code (§9.3.8).</summary>
    public bool Warning => (Word & Cia408StatusWord.Warning) != 0;

    /// <summary>Every bit above W, raw and unshifted: bit 6 (reserved), the control-mode specific
    /// bits 7 to 12, and the manufacturer-specific bits above them. Their meaning depends on the
    /// control mode and the manufacturer, so the peer does not name them.</summary>
    public uint OtherBits => Word & ~0x3Fu;
}

/// <summary>One entry of an observed PDO of a CiA 408 device, named.</summary>
/// <param name="Index">The object index as the mapping names it: absolute, in the logical device it
/// belongs to.</param>
/// <param name="Subindex">The sub-index as the mapping names it.</param>
/// <param name="Definition">The catalogue object, when the index is one of the profile in the logical
/// device of the peer that interpreted it; <see langword="null"/> otherwise.</param>
/// <param name="Description">What the entry carries — the object's name, with ", unit" or ", prefix"
/// for sub-index 02h or 03h of a value record — or the index and sub-index in hexadecimal,
/// <c>2000h:01h</c>, for an entry without a definition.</param>
/// <param name="Value">The mapped bits as an integer: sign-extended where the entry is of a signed
/// type (the value of a signed object, the prefix of a value record), zero-extended otherwise.</param>
internal readonly record struct Cia408PdoValue(
    ushort Index,
    byte Subindex,
    Cia408ObjectDefinition? Definition,
    string Description,
    long Value);

/// <summary>What <see cref="Cia408Peer.ObserveAsync"/> found: the core's result for the COB-ID and
/// the entries it decoded, named. <see cref="Values"/> is empty when nothing was decoded, and
/// <see cref="Result"/> says why.</summary>
/// <param name="Result">The result of <see cref="ICanOpenNode.ObserveForeignPdoAsync"/>.</param>
/// <param name="Values">The decoded entries in mapping order (<see cref="Cia408Peer.Interpret"/>).</param>
internal readonly record struct Cia408Observation(ForeignPdoObserveResult Result, IReadOnlyList<Cia408PdoValue> Values);

/// <summary>
/// The tool side of CiA 408 v2.2.0: typed access to one logical device of a foreign CiA 408 device —
/// its state machine, modes, error code, source of the control word and value records — made by
/// <see cref="Cia408Profile.CreatePeer"/>. It talks to nothing but <see cref="ICanOpenNode"/>: every
/// read and write is one SDO upload or download, so it passes the SDO client's peer gate, and without
/// a description of the device bound in the node (<see cref="ICanOpenNode.BindPeerDeviceDescription"/>)
/// the profile objects are refused with <see cref="PeerSdoAccessException"/> before a frame is sent.
/// </summary>
/// <remarks>
/// <para><b>Which word pair.</b> The norm calls both control/status word pairs Mandatory, while the
/// resolution says which one a device uses while it runs a control mode (§8.6.2, Table 53, NOTE 2;
/// the rule is <see cref="Cia408Configuration.IsWide(Cia408Resolution?, Cia408ControlMode)"/>, the one
/// the device side applies too). Where every mode the device type announces runs in one width, the
/// peer uses that width's pair. Where they differ — dtprs 11b with valve and drive modes — the pair
/// follows the mode the device runs, and the peer reads <c>6043h</c> at the start of every call that
/// touches the words (<see cref="UsesWideWords"/>).</para>
/// <para><b>Command sequences.</b> <see cref="EnableAsync"/>, <see cref="HoldAsync"/> and
/// <see cref="DisableAsync"/> walk the device one transition of Table 7 at a time from the state it
/// reports to the one asked for, writing one control word per transition and waiting for the status
/// it leads to before the next: up from INIT by <c>0001b</c> (D2), <c>0011b</c> (D3), <c>0111b</c>
/// (D4); down from ACTIVE by <c>0011b</c> (D5), <c>0001b</c> (D6). One transition per write needs no
/// assumption about whether a device follows a word through several transitions, which the norm
/// leaves open; a device that does follows each of these words exactly one transition, since the
/// next row needs the next word. The words carry bits 3..0 only; bits 6 and up, mode-specific and
/// manufacturer-specific, are written 0 — a tool that uses them writes its own words
/// (<see cref="WriteControlWordAsync"/>). <see cref="ResetFaultAsync"/> makes the fault reset of
/// Table 7 as an edge in two writes.</para>
/// <para><b>The wait.</b> Before the first write, and after each write, the peer reads the status
/// word at most <see cref="Cia408PeerOptions.StatusReads"/> times, with
/// <see cref="Cia408PeerOptions.StatusPause"/> before each read but the first, and goes on as soon as
/// the device shows the state it waits for. FAULT_REACTION, which a device passes through
/// (<see cref="Cia408Status"/>), is read through. A sequence fails with
/// <see cref="Cia408CommandException"/>, whose <see cref="Cia408CommandException.Reason"/> says why:
/// the device is in local control, is in a fault state or falls into one, or does not show the state
/// within the reads. A sequence that fails after it wrote leaves its last control word in the device,
/// which may keep moving.</para>
/// <para><b>Values.</b> They are encoded and decoded little-endian by the data type of the catalogue
/// (<see cref="CanOpenValueCodec"/>); a value of another length than its type is an
/// <see cref="InvalidDataException"/>, not a truncation.</para>
/// <para><b>Argument errors.</b> A null argument, a value outside its type, a control word with a
/// reserved bit, an object of the wrong kind and a write to an entry the catalogue marks read-only
/// are thrown by the call itself, before a task exists; nothing is sent then. Everything that happens
/// on the bus ends in the returned task.</para>
/// <para><b>State.</b> The peer holds the node, the peer's node-id, the logical device and its options,
/// and caches no value. It can be used from any thread; the node's SDO client allows one transfer per
/// server at a time and refuses a second with <see cref="InvalidOperationException"/>.</para>
/// </remarks>
internal sealed class Cia408Peer : ICanOpenProfilePeer
{
    // The states that have an encoding of their own (Table 4), in the order a status is matched;
    // NOT_READY shares 0000b with FAULT_INIT, and FAULT_REACTION is every other pattern with R = 0
    // (see Cia408Status).
    private static readonly Cia408State[] EncodedStates =
    {
        Cia408State.Init, Cia408State.Disabled, Cia408State.Hold, Cia408State.Active,
        Cia408State.FaultInit, Cia408State.FaultDisabled, Cia408State.FaultHold,
    };

    // The states the command sequences walk, from the lowest, and the control word that leads into
    // each from its neighbour on either side (Table 7: D2 and D6 into DISABLED, D3 and D5 into HOLD,
    // D4 into ACTIVE, D7 into INIT).
    private static readonly Cia408State[] Ladder = { Cia408State.Init, Cia408State.Disabled, Cia408State.Hold, Cia408State.Active };
    private static readonly uint[] LadderWords = { 0b0000, 0b0001, 0b0011, 0b0111 };

    private readonly ICanOpenNode _node;
    private readonly Cia408PeerOptions _options;

    internal Cia408Peer(ICanOpenProfile profile, ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice,
        Cia408PeerOptions options)
    {
        Profile = profile;
        _node = node;
        _options = options;
        PeerNodeId = peerNodeId;
        LogicalDevice = logicalDevice;
        SupportedControlModes = Cia408Configuration.ControlModesOf(logicalDevice.DeviceType);
        Resolution = Cia408Configuration.ResolutionOf(logicalDevice.DeviceType);

        int wide = 0;
        foreach (var mode in SupportedControlModes) wide += Cia408Configuration.IsWide(Resolution, mode) ? 1 : 0;
        UsesWideWords = SupportedControlModes.Count == 0 ? Resolution == Cia408Resolution.Valve32Drive32
            : wide == SupportedControlModes.Count ? true
            : wide == 0 ? false
            : null;
    }

    /// <inheritdoc />
    public ICanOpenProfile Profile { get; }

    /// <inheritdoc />
    public byte PeerNodeId { get; }

    /// <inheritdoc />
    public CanOpenLogicalDevice LogicalDevice { get; }

    /// <summary>The control modes the device type announces with its capability bits (§8.6.2,
    /// Table 53), ascending; bits 23 and 24 each give two modes, and the drive bits of a 16-bit device
    /// type none (<see cref="Cia408Configuration.ControlModesOf"/>). What the device runs now is
    /// <c>6043h</c> (<see cref="ReadControlModeAsync"/>). The modes are reported as the type
    /// announces them, also pQ or pFlow on a 32-bit valve, which the norm maps in 16 bit only and a
    /// device of this package refuses (<see cref="Cia408Configuration.IsMappedIn16BitOnly"/>).</summary>
    public IReadOnlyList<Cia408ControlMode> SupportedControlModes { get; }

    /// <summary>The resolution field dtprs of the device type (Table 53), or <see langword="null"/>
    /// for the reserved code 00b.</summary>
    public Cia408Resolution? Resolution { get; }

    /// <summary>Whether the peer reads and writes the 32-bit pair <c>6044h</c>/<c>6045h</c>
    /// (<see langword="true"/>) or <c>6040h</c>/<c>6041h</c> (<see langword="false"/>):
    /// <see cref="Cia408Configuration.IsWide(Cia408Resolution?, Cia408ControlMode)"/> for every mode the
    /// device type announces, the resolution's valve width where it announces none.
    /// <see langword="null"/> where the modes differ in width: the pair is then that of the mode in
    /// <c>6043h</c>, read at the start of each call that touches the words. <c>6043h</c> is Optional
    /// (§9.3.5); a device that answers it with <c>0602 0000h</c>, "object does not exist", runs its
    /// default control mode, which the norm does not name: the peer takes the lowest mode the device
    /// type announces (<see cref="SupportedControlModes"/>), the mode a device built with
    /// <see cref="Cia408Device.FromDescription"/> runs without <c>6043h</c>.</summary>
    public bool? UsesWideWords { get; }

    // ---- the state machine ---------------------------------------------------------------------------

    /// <summary>Reads the status word and decodes it (<see cref="Cia408Status"/>). Where the word pair
    /// depends on the control mode (<see cref="UsesWideWords"/> is null), reads <c>6043h</c> first.</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted an upload.</exception>
    /// <exception cref="InvalidDataException">An entry has another length than its type, or the status
    /// word has R = 1 with bits 2..0 that encode no state of Table 4.</exception>
    public Task<Cia408Status> ReadStatusAsync(CancellationToken cancellationToken = default)
        => ReadStatusWithWordsAsync(cancellationToken);

    private async Task<Cia408Status> ReadStatusWithWordsAsync(CancellationToken cancellationToken)
    {
        var words = await WordsAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatusCoreAsync(words, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Cia408Status> ReadStatusCoreAsync(Words words, CancellationToken cancellationToken)
    {
        var word = (uint)CanOpenValueCodec.Decode(words.Status.ValueType,
            await ReadAsync(words.Status, 0x00, cancellationToken).ConfigureAwait(false));
        var bits = (byte)(word & Cia408ControlWord.MachineBits);
        foreach (var state in EncodedStates)
        {
            if (Cia408StateMachine.StatusBits(state) == bits) return new Cia408Status(word, state);
        }

        if ((bits & Cia408ControlWord.ResetFault) == 0) return new Cia408Status(word, Cia408State.FaultReaction);

        throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
            "{0} is {1:X4}h: bits 3..0 = {2} encode no state of CiA 408 §7.6, Table 4.",
            Describe(words.Status.Index, 0x00), word, Convert.ToString(bits, 2).PadLeft(4, '0')));
    }

    /// <summary>Writes <paramref name="word"/> to the control word as given, with no wait.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="word"/> sets a reserved bit — 4,
    /// 5 or 12, and 24 to 31 of the 32-bit word (§9.3.2, Table 73; §9.3.6, Table 85) — or a bit above
    /// 15 where the peer uses the 16-bit word. Thrown by the call where the pair is known
    /// (<see cref="UsesWideWords"/>), in the returned task where it follows <c>6043h</c>.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted a transfer.</exception>
    public Task WriteControlWordAsync(uint word, CancellationToken cancellationToken = default)
    {
        RequireNoReservedBit(word, UsesWideWords ?? true);
        return WriteControlWordCoreAsync(word, cancellationToken);
    }

    private async Task WriteControlWordCoreAsync(uint word, CancellationToken cancellationToken)
    {
        var words = await WordsAsync(cancellationToken).ConfigureAwait(false);
        RequireNoReservedBit(word, words.Wide);
        await WriteWordAsync(words, word, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireNoReservedBit(uint word, bool wide)
    {
        uint reserved = wide ? Cia408ControlWord.Reserved32 : Cia408ControlWord.Reserved16 | 0xFFFF_0000u;
        if ((word & reserved) == 0) return;
        throw new ArgumentOutOfRangeException(nameof(word), word, string.Format(CultureInfo.InvariantCulture,
            "{0:X8}h sets a bit the {1}-bit control word reserves or does not have (CiA 408 §9.3.2, Table 73; §9.3.6, Table 85).",
            word, wide ? 32 : 16));
    }

    private Task WriteWordAsync(Words words, uint word, CancellationToken cancellationToken)
        => _node.SdoDownloadAsync(PeerNodeId, Shift(words.Control), 0x00, CanOpenValueCodec.Encode(words.Control.ValueType, word),
            SdoTransferMode.Auto, cancellationToken);

    /// <summary>The word pair of this call (<see cref="UsesWideWords"/>): fixed by the device type, or
    /// that of the control mode in <c>6043h</c>, read now.</summary>
    private async Task<Words> WordsAsync(CancellationToken cancellationToken)
    {
        bool wide = UsesWideWords ?? Cia408Configuration.IsWide(Resolution, await RunningModeAsync(cancellationToken).ConfigureAwait(false));
        return wide
            ? new Words(true, Cia408Objects.DeviceControlWord32, Cia408Objects.DeviceStatusWord32)
            : new Words(false, Cia408Objects.DeviceControlWord16, Cia408Objects.DeviceStatusWord16);
    }

    /// <summary>The control mode the device runs: <c>6043h</c>, or, where the device has no such
    /// object, the lowest mode its type announces (see <see cref="UsesWideWords"/>).</summary>
    private async Task<Cia408ControlMode> RunningModeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadVariableAsync(Cia408Objects.DeviceControlMode, static value => (Cia408ControlMode)value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SdoAbortException abort) when (abort.AbortCode == (uint)SdoAbortCode.ObjectDoesNotExist && SupportedControlModes.Count > 0)
        {
            return SupportedControlModes[0];
        }
    }

    /// <summary>Takes the device to ACTIVE, one transition of Table 7 at a time from the state it
    /// reports (see the remarks on the class): from INIT D2, D3, D4; from DISABLED D3, D4; from HOLD
    /// D4; from ACTIVE nothing is written.</summary>
    /// <exception cref="Cia408CommandException">The device is in local control, in a fault state
    /// (reset it first, <see cref="ResetFaultAsync"/>), falls into one, or does not show the next state
    /// within the reads the options allow; <see cref="Cia408CommandException.Reason"/> says which.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted a transfer.</exception>
    /// <exception cref="InvalidDataException">The status word encodes no state (see <see cref="Cia408Status"/>).</exception>
    public Task EnableAsync(CancellationToken cancellationToken = default) => WalkAsync(Cia408State.Active, cancellationToken);

    /// <summary>Takes the device to HOLD, one transition at a time: from INIT D2, D3; from DISABLED
    /// D3; from ACTIVE D5.</summary>
    /// <exception cref="Cia408CommandException">As for <see cref="EnableAsync"/>.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted a transfer.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="EnableAsync"/>.</exception>
    public Task HoldAsync(CancellationToken cancellationToken = default) => WalkAsync(Cia408State.Hold, cancellationToken);

    /// <summary>Takes the device to DISABLED, one transition at a time: from INIT D2; from HOLD D6;
    /// from ACTIVE D5, D6.</summary>
    /// <exception cref="Cia408CommandException">As for <see cref="EnableAsync"/>.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted a transfer.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="EnableAsync"/>.</exception>
    public Task DisableAsync(CancellationToken cancellationToken = default) => WalkAsync(Cia408State.Disabled, cancellationToken);

    private async Task WalkAsync(Cia408State target, CancellationToken cancellationToken)
    {
        var words = await WordsAsync(cancellationToken).ConfigureAwait(false);
        var status = await WaitForAsync(words, IsRestingState, target, cancellationToken).ConfigureAwait(false);
        if (IsFaultState(status.State))
        {
            throw Failure(Cia408CommandFailure.FaultState, status.State, string.Format(CultureInfo.InvariantCulture,
                "is in {0}: a fault state is left by a fault reset (ResetFaultAsync), not by the control words of {1} " +
                "(CiA 408 §7.7, Table 7). Nothing was written.", status.State, target));
        }

        int at = Array.IndexOf(Ladder, status.State);
        int to = Array.IndexOf(Ladder, target);
        while (at != to)
        {
            at += at < to ? 1 : -1;
            var next = Ladder[at];
            await WriteWordAsync(words, LadderWords[at], cancellationToken).ConfigureAwait(false);
            var reached = await WaitForAsync(words, state => state == next || IsFaultState(state), next, cancellationToken)
                .ConfigureAwait(false);
            if (reached.State != next)
            {
                throw Failure(Cia408CommandFailure.FaultState, reached.State, string.Format(CultureInfo.InvariantCulture,
                    "fell into {0} on the way to {1}.", reached.State, next));
            }
        }
    }

    /// <summary>
    /// Resets the fault of a device in a fault state (§7.7, Table 7): writes the fault state's own
    /// pattern with R (bit 3) clear, then the same pattern with R set — <c>0001b</c> then
    /// <c>1001b</c> from FAULT_DISABLED (D10), <c>0011b</c> then <c>1011b</c> from FAULT_HOLD (D11),
    /// <c>0000b</c> then <c>1000b</c> from FAULT_INIT (D14) — and waits for DISABLED, HOLD or INIT.
    /// </summary>
    /// <remarks>
    /// <para>Two writes, because the reset is an edge of R between two control words whose bits 2..0
    /// are the pattern, and the word the device holds may be anything — the word it faulted under,
    /// say. The first write meets no row of Table 7 in its fault state (D15 needs <c>x001b</c> in
    /// FAULT_HOLD, D16 <c>x000b</c> in FAULT_DISABLED), so it moves nothing by itself.</para>
    /// <para>A device still in FAULT_REACTION is read through until it rests in its fault state. A
    /// device may refuse the reset while the cause of the fault lasts — CiA 408 does not say, and
    /// <see cref="Cia408Device"/> refuses. Such a device stays in its fault state, and the call fails with
    /// <see cref="Cia408CommandFailure.NotReached"/> once the reads are used up.</para>
    /// </remarks>
    /// <exception cref="Cia408CommandException">The device is in local control, in no fault state
    /// (<see cref="Cia408CommandFailure.NoFault"/>; nothing was written), or not out of its fault state
    /// within the reads the options allow.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted a transfer.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="EnableAsync"/>.</exception>
    public Task ResetFaultAsync(CancellationToken cancellationToken = default) => ResetFaultCoreAsync(cancellationToken);

    private async Task ResetFaultCoreAsync(CancellationToken cancellationToken)
    {
        var words = await WordsAsync(cancellationToken).ConfigureAwait(false);
        var status = await WaitForAsync(words, IsRestingState, null, cancellationToken).ConfigureAwait(false);
        var expected = status.State switch
        {
            Cia408State.FaultDisabled => Cia408State.Disabled,
            Cia408State.FaultHold => Cia408State.Hold,
            Cia408State.FaultInit => Cia408State.Init,
            _ => throw Failure(Cia408CommandFailure.NoFault, status.State, string.Format(CultureInfo.InvariantCulture,
                "is in {0}, not in a fault state: there is no fault to reset. Nothing was written.", status.State)),
        };

        uint pattern = Cia408StateMachine.StatusBits(status.State);
        await WriteWordAsync(words, pattern, cancellationToken).ConfigureAwait(false);
        await WriteWordAsync(words, pattern | Cia408ControlWord.ResetFault, cancellationToken).ConfigureAwait(false);
        await WaitForAsync(words, state => state == expected, expected, cancellationToken, refusedReset: status.State).ConfigureAwait(false);
    }

    /// <summary>Reads the status until <paramref name="done"/> holds for its state, at most
    /// <see cref="Cia408PeerOptions.StatusReads"/> times with <see cref="Cia408PeerOptions.StatusPause"/>
    /// before each read but the first, and returns it; a status in local control fails at once (see the
    /// remarks on the class). <paramref name="aim"/> names the state for the message.</summary>
    private async Task<Cia408Status> WaitForAsync(Words words, Func<Cia408State, bool> done, Cia408State? aim,
        CancellationToken cancellationToken, Cia408State? refusedReset = null)
    {
        Cia408Status status = default;
        for (int read = 0; read < _options.StatusReads; read++)
        {
            if (read > 0 && _options.StatusPause > TimeSpan.Zero)
                await _options.Delay(_options.StatusPause, cancellationToken).ConfigureAwait(false);
            status = await ReadStatusCoreAsync(words, cancellationToken).ConfigureAwait(false);
            if (status.Local)
            {
                throw Failure(Cia408CommandFailure.LocalControl, status.State,
                    "is in local control (status bit 4; 604Fh = 1, CiA 408 §9.3.9): it ignores a control word written over the " +
                    "bus. Write 604Fh = 0 first (WriteLocalAsync). Nothing further was written.");
            }

            if (done(status.State)) return status;
        }

        throw Failure(Cia408CommandFailure.NotReached, status.State, refusedReset is { } fault
            ? string.Format(CultureInfo.InvariantCulture,
                "refused the fault reset: it is still in {0} after {1} reads of the status word. A device may refuse the reset " +
                "while the cause of the fault lasts.", fault, _options.StatusReads)
            : string.Format(CultureInfo.InvariantCulture,
                "did not show {0} within {1} reads of the status word: it shows {2}, and may still be moving.",
                aim is { } state ? state.ToString() : "a resting state", _options.StatusReads, status.State));
    }

    private Cia408CommandException Failure(Cia408CommandFailure reason, Cia408State state, string what)
        => new(reason, state, string.Format(CultureInfo.InvariantCulture,
            "Node 0x{0:X2}, logical device {1}, {2}", PeerNodeId, LogicalDevice.Index, what));

    private static bool IsFaultState(Cia408State state)
        => state is Cia408State.FaultInit or Cia408State.FaultDisabled or Cia408State.FaultHold;

    /// <summary>A state the device may rest in: every state but FAULT_REACTION, which it passes through
    /// (and NOT_READY, which no status shows; see <see cref="Cia408Status"/>).</summary>
    private static bool IsRestingState(Cia408State state) => state != Cia408State.FaultReaction;

    /// <summary>The control and status word of one call, and whether they are the 32-bit pair.</summary>
    private readonly record struct Words(bool Wide, Cia408ObjectDefinition Control, Cia408ObjectDefinition Status);

    // ---- modes, error code, source -------------------------------------------------------------------

    /// <summary>Reads <c>6042h</c>, the device mode (§9.3.4). A value outside Table 79 is returned as
    /// it is (a manufacturer-specific mode, say).</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<Cia408DeviceMode> ReadDeviceModeAsync(CancellationToken cancellationToken = default)
        => ReadVariableAsync(Cia408Objects.DeviceMode, static value => (Cia408DeviceMode)value, cancellationToken);

    /// <summary>Writes <c>6042h</c>. The device decides what it takes: an unsupported mode or one the
    /// state does not let change (ACTIVE, Table 4) is its abort.</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download.</exception>
    public Task WriteDeviceModeAsync(Cia408DeviceMode mode, CancellationToken cancellationToken = default)
        => WriteVariableAsync(Cia408Objects.DeviceMode, (sbyte)mode, cancellationToken);

    /// <summary>Reads <c>6043h</c>, the control mode (§9.3.5), returned as it is.</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<Cia408ControlMode> ReadControlModeAsync(CancellationToken cancellationToken = default)
        => ReadVariableAsync(Cia408Objects.DeviceControlMode, static value => (Cia408ControlMode)value, cancellationToken);

    /// <summary>Writes <c>6043h</c>. The device decides: it is read-only with one control mode
    /// (§9.3.5) and changeable in some states only (Table 5).</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download.</exception>
    public Task WriteControlModeAsync(Cia408ControlMode mode, CancellationToken cancellationToken = default)
        => WriteVariableAsync(Cia408Objects.DeviceControlMode, (sbyte)mode, cancellationToken);

    /// <summary>Reads <c>604Eh</c>, the device error code (§9.3.8): the warning or fault that
    /// occurred.</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<ushort> ReadErrorCodeAsync(CancellationToken cancellationToken = default)
        => ReadVariableAsync(Cia408Objects.DeviceErrorCode, static value => (ushort)value, cancellationToken);

    /// <summary>Reads <c>604Fh</c>, device local (§9.3.9): <see langword="true"/> when the control
    /// word comes from the device's local source (1), <see langword="false"/> when over the bus
    /// (0).</summary>
    /// <exception cref="InvalidDataException">The device holds another value: reserved, or
    /// manufacturer-specific (Table 93), which the peer does not interpret.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<bool> ReadLocalAsync(CancellationToken cancellationToken = default)
        => ReadVariableAsync(Cia408Objects.DeviceLocal, static value => value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "604Fh is {0}: only 0 (bus) and 1 (local) are interpreted (CiA 408 §9.3.9, Table 93).", value)),
        }, cancellationToken);

    /// <summary>Writes <c>604Fh</c>: 1 for the local source, 0 for the bus.</summary>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download (a device whose source
    /// cannot be switched over the bus has the object read-only).</exception>
    public Task WriteLocalAsync(bool local, CancellationToken cancellationToken = default)
        => WriteVariableAsync(Cia408Objects.DeviceLocal, local ? 1 : 0, cancellationToken);

    private async Task<T> ReadVariableAsync<T>(Cia408ObjectDefinition definition, Func<long, T> convert, CancellationToken cancellationToken)
        => convert(CanOpenValueCodec.Decode(definition.ValueType, await ReadAsync(definition, 0x00, cancellationToken).ConfigureAwait(false)));

    private Task WriteVariableAsync(Cia408ObjectDefinition definition, long value, CancellationToken cancellationToken)
        => _node.SdoDownloadAsync(PeerNodeId, Shift(definition), 0x00, CanOpenValueCodec.Encode(definition.ValueType, value),
            SdoTransferMode.Auto, cancellationToken);

    // ---- value records -------------------------------------------------------------------------------

    /// <summary>Reads a value record (§8.7.1) as the device reports it: the value at <c>01h</c>, the
    /// unit at <c>02h</c> and the prefix at <c>03h</c>, three uploads. Unit and prefix are read, not
    /// taken from the catalogue: the manufacturer may fix or configure them (§8.3).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is a <c>VAR</c>, which has
    /// no unit or prefix: read it with <see cref="ReadAsync"/>.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow an entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted an upload (a device without the optional
    /// unit or prefix, say).</exception>
    /// <exception cref="InvalidDataException">An entry has another length than its type.</exception>
    public Task<Cia408ValueRecord> ReadValueAsync(Cia408ObjectDefinition definition, CancellationToken cancellationToken = default)
    {
        RequireValueRecord(definition);
        return ReadValueCoreAsync(definition, cancellationToken);
    }

    private async Task<Cia408ValueRecord> ReadValueCoreAsync(Cia408ObjectDefinition definition, CancellationToken cancellationToken)
    {
        long value = CanOpenValueCodec.Decode(definition.ValueType, await ReadAsync(definition, 0x01, cancellationToken).ConfigureAwait(false));
        long unit = CanOpenValueCodec.Decode(OdDataType.Unsigned8, await ReadAsync(definition, 0x02, cancellationToken).ConfigureAwait(false));
        long prefix = CanOpenValueCodec.Decode(OdDataType.Integer8, await ReadAsync(definition, 0x03, cancellationToken).ConfigureAwait(false));
        return new Cia408ValueRecord(value, (byte)unit, (sbyte)prefix);
    }

    /// <summary>Writes the value (sub-index <c>01h</c>) of a value record, in the record's value
    /// type.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is a <c>VAR</c>: write it with
    /// <see cref="WriteAsync"/> or its typed member.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> lies outside the value
    /// type (<see cref="CanOpenValueCodec.IsInRange"/>).</exception>
    /// <exception cref="InvalidOperationException">The catalogue marks the value read-only (an actual
    /// value).</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download.</exception>
    public Task WriteValueAsync(Cia408ObjectDefinition definition, long value, CancellationToken cancellationToken = default)
    {
        RequireValueRecord(definition);
        if (!CanOpenValueCodec.IsInRange(definition.ValueType, value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, string.Format(CultureInfo.InvariantCulture,
                "{0} is {1}: {2} does not fit.", definition, definition.ValueType, value));
        }

        return WriteAsync(definition, 0x01, CanOpenValueCodec.Encode(definition.ValueType, value), cancellationToken);
    }

    private static void RequireValueRecord(Cia408ObjectDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (definition.IsValueRecord) return;
        throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
            "{0} is a VAR, not a value record (CiA 408 §8.7.1): it has no unit and no prefix.", definition), nameof(definition));
    }

    // ---- any catalogue object ------------------------------------------------------------------------

    /// <summary>Uploads sub-index <paramref name="subindex"/> of any catalogue object, in the logical
    /// device of the peer, and returns its bytes as the device sent them. The device need not implement
    /// the object: the gate and the device decide.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<byte[]> ReadAsync(Cia408ObjectDefinition definition, byte subindex, CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        return _node.SdoUploadAsync(PeerNodeId, Shift(definition), subindex, SdoTransferMode.Auto, cancellationToken);
    }

    /// <summary>Downloads <paramref name="value"/> to sub-index <paramref name="subindex"/> of any
    /// catalogue object, in the logical device of the peer, as given.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or
    /// <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The catalogue marks the entry read-only — the value
    /// of a <c>ro</c> object (sub-index <c>00h</c> of a <c>VAR</c>, <c>01h</c> of a value record), or
    /// sub-index <c>00h</c> of a value record, which is ro in every entry description of clause 9 —
    /// and nothing was sent. Unit and prefix, "rw or ro" (§8.3), are the device's to refuse.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download.</exception>
    public Task WriteAsync(Cia408ObjectDefinition definition, byte subindex, byte[] value, CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (value is null) throw new ArgumentNullException(nameof(value));
        byte valueEntry = definition.IsValueRecord ? (byte)0x01 : (byte)0x00;
        bool readOnly = (subindex == valueEntry && (definition.Access & OdAccess.WriteOnly) == 0)
            || (definition.IsValueRecord && subindex == 0x00);
        if (readOnly)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "{0} is read-only: it is not written.", Describe(definition.Index, subindex)));
        }

        return _node.SdoDownloadAsync(PeerNodeId, Shift(definition), subindex, value, SdoTransferMode.Auto, cancellationToken);
    }

    // ---- PDOs ----------------------------------------------------------------------------------------

    /// <summary>
    /// Names the entries of an observed PDO of this peer, in the order given: the order in which
    /// <see cref="ICanOpenNode.ObserveForeignPdoAsync"/> delivered them to its sink, which is the
    /// order of the mapping the PDO really had (the device's live mapping before the description's).
    /// Pure: no bus access.
    /// </summary>
    /// <remarks>
    /// <para>An entry whose index, shifted back by the logical device of this peer, is a catalogue
    /// object gets its definition and its name. Its value is signed or unsigned by the entry's type:
    /// the catalogue's value type for the value (sub-index <c>00h</c> of a <c>VAR</c>, <c>01h</c> of a
    /// value record), INTEGER8 for the prefix (<c>03h</c>), unsigned for the unit (<c>02h</c>) and any
    /// other sub-index. Every other entry — an object outside the profile, a reserved index, an object
    /// of another logical device — is kept with no definition, its index and sub-index in hexadecimal
    /// and its value unsigned in the mapped width.</para>
    /// <para>Dummy entries (<c>0001h</c> to <c>0007h</c>, CiA 301 v4.2.0 §7.4.7.1) occupy their bits in
    /// the PDO, but the core delivers no signal for them, so they never reach this method.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="signals"/> is null.</exception>
    /// <exception cref="ArgumentException">A signal comes from another node than this peer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A signal has a bit length outside 1 to 64.</exception>
    /// <exception cref="InvalidDataException">A signal has fewer bytes than its bit length needs.</exception>
    public Cia408PdoValue[] Interpret(IReadOnlyList<ForeignPdoSignal> signals)
    {
        if (signals is null) throw new ArgumentNullException(nameof(signals));

        var values = new Cia408PdoValue[signals.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var signal = signals[i];
            if (signal.PeerNodeId != PeerNodeId)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "Signal {0} comes from node 0x{1:X2}, not from this peer, node 0x{2:X2}.", i, signal.PeerNodeId, PeerNodeId),
                    nameof(signals));
            }

            values[i] = TryFindDefinition(signal.Index, out var definition)
                ? new Cia408PdoValue(signal.Index, signal.SubIndex, definition, Describe(definition.Index, signal.SubIndex),
                    CanOpenValueCodec.DecodeBits(signal.Value, signal.BitLength, IsSignedEntry(definition, signal.SubIndex)))
                : new Cia408PdoValue(signal.Index, signal.SubIndex, null,
                    string.Format(CultureInfo.InvariantCulture, "{0:X4}h:{1:X2}h", signal.Index, signal.SubIndex),
                    CanOpenValueCodec.DecodeBits(signal.Value, signal.BitLength, signed: false));
        }

        return values;
    }

    /// <summary>
    /// Observes one PDO payload of this peer with <see cref="ICanOpenNode.ObserveForeignPdoAsync"/> —
    /// which reads the device's COB-IDs and mapping live over SDO, through the gate, and takes
    /// <paramref name="peerDescription"/> where a live read fails — and names what it carried with
    /// <see cref="Interpret"/>. When the core decodes nothing, <see cref="Cia408Observation.Values"/>
    /// is empty and <see cref="Cia408Observation.Result"/> says why; that is not an exception.
    /// </summary>
    /// <param name="cobId">The observed ID, with bit 29 set for an extended frame.</param>
    /// <param name="payload">The PDO data, 0 to 8 bytes.</param>
    /// <param name="peerDescription">EDS or DCF of the peer, as the core method takes it.</param>
    /// <param name="cancellationToken">Cancels an in-flight mapping upload.</param>
    /// <exception cref="ArgumentNullException"><paramref name="peerDescription"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">As for the core method, in the returned task.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled, in the returned task.</exception>
    public Task<Cia408Observation> ObserveAsync(uint cobId, ReadOnlyMemory<byte> payload,
        CanOpenDeviceDescription peerDescription, CancellationToken cancellationToken = default)
    {
        if (peerDescription is null) throw new ArgumentNullException(nameof(peerDescription));
        return ObserveCoreAsync(cobId, payload, peerDescription, cancellationToken);
    }

    private async Task<Cia408Observation> ObserveCoreAsync(uint cobId, ReadOnlyMemory<byte> payload,
        CanOpenDeviceDescription peerDescription, CancellationToken cancellationToken)
    {
        var sink = new ListSink();
        var result = await _node.ObserveForeignPdoAsync(PeerNodeId, cobId, payload, peerDescription, sink, cancellationToken)
            .ConfigureAwait(false);
        return new Cia408Observation(result, Interpret(sink.Signals));
    }

    // ---- shared --------------------------------------------------------------------------------------

    private ushort Shift(Cia408ObjectDefinition definition)
        => CanOpenLogicalDeviceAddressing.Index(LogicalDevice.Index, definition.Index);

    /// <summary>The catalogue object whose index, in the logical device of this peer, is
    /// <paramref name="absoluteIndex"/>.</summary>
    private bool TryFindDefinition(ushort absoluteIndex, out Cia408ObjectDefinition definition)
    {
        const ushort first = 0x6000;
        const ushort size = 0x800;
        int indexInDevice0 = absoluteIndex - (CanOpenLogicalDeviceAddressing.Index(LogicalDevice.Index, first) - first);
        if (indexInDevice0 is >= first and < first + size) return Cia408Objects.TryGet((ushort)indexInDevice0, out definition);
        definition = null!;
        return false;
    }

    private static bool IsSignedEntry(Cia408ObjectDefinition definition, byte subindex)
    {
        if (subindex == (definition.IsValueRecord ? 0x01 : 0x00)) return CanOpenValueCodec.IsSigned(definition.ValueType);
        return definition.IsValueRecord && subindex == 0x03;
    }

    /// <summary>An entry of a catalogue object, by its index in logical device 0, for messages and
    /// <see cref="Cia408PdoValue.Description"/>.</summary>
    private static string Describe(ushort indexInDevice0, byte subindex)
    {
        if (!Cia408Objects.TryGet(indexInDevice0, out var definition))
            return string.Format(CultureInfo.InvariantCulture, "{0:X4}h:{1:X2}h", indexInDevice0, subindex);
        return (definition.IsValueRecord, subindex) switch
        {
            (false, 0x00) or (true, 0x01) => definition.Name,
            (true, 0x00) => definition.Name + ", highest sub-index supported",
            (true, 0x02) => definition.Name + ", unit",
            (true, 0x03) => definition.Name + ", prefix",
            _ => string.Format(CultureInfo.InvariantCulture, "{0}, sub-index {1:X2}h", definition.Name, subindex),
        };
    }

    private sealed class ListSink : IForeignPdoSink
    {
        internal List<ForeignPdoSignal> Signals { get; } = new();

        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }
}
