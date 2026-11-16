using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-011, CiA 301 §7.5.2.32: the mandatory communication-failure sources are bus-off,
// heartbeat timeout and local life-guarding timeout. EMCY reporting is an independent option.
internal sealed partial class CanOpenNode
{
    private byte _communicationErrorBehavior;

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
        if (!_controllerObservedBusOff && _heartbeatEmcyTimeouts.Count == 0 && !_lifeGuardingOccurred) return target;
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
