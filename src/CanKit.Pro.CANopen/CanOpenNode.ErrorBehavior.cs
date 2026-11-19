using System;
using System.Linq;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-011, CiA 301 §7.5.2.32: the mandatory communication-failure sources are bus-off,
// heartbeat timeout and local life-guarding timeout. EMCY reporting is an independent option.
internal sealed partial class CanOpenNode
{
    private byte _communicationErrorBehavior;

    // The defaults of the error classes an application laid out for 1029h
    // (CanOpenApplicationRequirements.ErrorBehavior): [0] is sub-index 01h, the communication error,
    // and [n] sub-index n + 1; so the highest sub-index is the length. Null for the generic record,
    // which is all a node without applications ever has. Written once, on the constructing thread,
    // before the dictionary is built (ReadApplicationRequirements).
    private byte[]? _errorBehaviorLayout;

    // The application that laid it out first, for the messages that name it.
    private string? _errorBehaviorLayoutOwner;

    // The value range of every error class: Table 63 of CiA 301 v4.2.0 §7.5.2.32 defines 00h to 02h,
    // reserves 03h to 7Fh and leaves 80h to FFh to the manufacturer; the node implements 00h to 02h.
    private const byte HighestErrorClassValue = 2;

    // The error classes a record can hold: sub-indices 01h to FEh (§7.5.2.32).
    private const int MostErrorClasses = 0xFE;

    /// <summary>Takes the layout <paramref name="application"/> asks for, checked: values within
    /// Table 63, at most 254 classes, and the same as any layout an earlier application asked for.
    /// Constructing thread, before the dictionary is built.</summary>
    /// <exception cref="ArgumentException">A check fails; the parameter is <c>options</c>.</exception>
    private void TakeErrorBehaviorLayout(ICanOpenApplication application, CanOpenErrorBehaviorLayout layout)
    {
        var additional = layout.AdditionalDefaults
            ?? throw OptionsError($"Application '{application.Name}' lays out 1029h with null AdditionalDefaults: " +
                "a layout without further error classes gives an empty list.");
        if (additional.Count + 1 > MostErrorClasses)
        {
            throw OptionsError($"Application '{application.Name}' lays out 1029h with {additional.Count} additional error classes: " +
                "sub-indices 02h to FEh hold at most 253 (CiA 301 v4.2.0 §7.5.2.32).");
        }
        var defaults = new byte[additional.Count + 1];
        defaults[0] = layout.CommunicationErrorDefault;
        for (int i = 0; i < additional.Count; i++) defaults[i + 1] = additional[i];
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i] <= HighestErrorClassValue) continue;
            throw OptionsError($"Application '{application.Name}' lays out 1029h with {defaults[i]:X2}h at sub-index {i + 1:X2}h: " +
                "an error class takes 00h, 01h or 02h (CiA 301 v4.2.0 §7.5.2.32, Table 63).");
        }
        if (_errorBehaviorLayout is null)
        {
            _errorBehaviorLayout = defaults;
            _errorBehaviorLayoutOwner = application.Name;
            return;
        }
        if (_errorBehaviorLayout.SequenceEqual(defaults)) return;
        throw OptionsError($"Applications '{_errorBehaviorLayoutOwner}' and '{application.Name}' lay out 1029h differently: " +
            "a node has one error behaviour record.");
    }

    /// <summary>Declares <c>1029h</c> with <c>EnableErrorBehavior</c>: the generic record (sub-index
    /// 00h = 01h, the communication error at 01h, default 00h, CiA 301 v4.2.0 §7.5.2.32), or the one
    /// an application laid out. Constructing thread, while the communication profile is populated.</summary>
    private void PopulateErrorBehavior(OdAccess ro, OdAccess rw, bool pdoMappable)
    {
        if (_errorBehaviorLayout is not { } layout)
        {
            _od.AddU8(Co.ErrorBehavior, 0, 1, ro, pdoMappable);
            _od.AddU8(Co.ErrorBehavior, 1, 0, rw, pdoMappable);
            return;
        }
        _od.AddU8(Co.ErrorBehavior, 0, (byte)layout.Length, ro, pdoMappable);
        for (int i = 0; i < layout.Length; i++) _od.AddU8(Co.ErrorBehavior, (byte)(i + 1), layout[i], rw, pdoMappable);
    }

    /// <summary>What the error class at <c>1029h:<paramref name="subindex"/></c> asks for now
    /// (<see cref="ICanOpenApplicationHost.ErrorBehaviorTarget"/>): Table 63 of CiA 301 v4.2.0
    /// §7.5.2.32 against the present state. Actor loop.</summary>
    private NmtState? ErrorBehaviorTargetOf(byte subindex)
    {
        if (!_od.TryReadRaw(Co.ErrorBehavior, subindex, out var raw) || raw.Length != 1) return null;
        return raw[0] switch
        {
            0 when _state == NmtState.Operational => NmtState.PreOperational,
            2 when _state is not (NmtState.Stopped or NmtState.Initializing) => NmtState.Stopped,
            _ => null,
        };
    }

    private void ApplyErrorBehaviorConfiguration()
    {
        if (!_options.EnableErrorBehavior) return;
        _communicationErrorBehavior = (byte)_od.ReadUnsigned(Co.ErrorBehavior, 1);
        ApplyCommunicationErrorBehavior();
    }

    private static OdWriteDecision ValidateErrorBehaviorWrite(byte subindex, byte[] value)
        => subindex == 0 ? OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly)
            : value[0] <= 2 ? OdWriteDecision.Accept
            : OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);

    private NmtState ResolveErrorBehaviorTarget(NmtState target)
    {
        if (!_options.EnableErrorBehavior || target != NmtState.Operational) return target;
        if (!CommunicationFailure) return target;
        return _communicationErrorBehavior switch
        {
            0 => NmtState.PreOperational,
            2 => NmtState.Stopped,
            _ => target,
        };
    }

    private void ApplyCommunicationErrorBehavior()
    {
        if (_state != NmtState.Operational) return;
        var target = ResolveErrorBehaviorTarget(_state);
        if (target != _state) ApplyNmtTransition(target);
    }
}
