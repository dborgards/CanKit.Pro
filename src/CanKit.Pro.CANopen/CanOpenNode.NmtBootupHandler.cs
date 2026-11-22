using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private void ObserveNmtBootup(uint cobId, byte[] data, bool isRtr, bool isFd)
    {
        if (!_options.EnableNmtBootupHandler || _disposed != 0 || _flyingMasterRole != FlyingMasterRole.Active
            || cobId <= CanOpenCobId.HeartbeatBase || cobId > CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId
            || isRtr || isFd || data.Length != 1 || data[0] != 0) return;
        byte nodeId = (byte)(cobId - CanOpenCobId.HeartbeatBase);
        if (nodeId == _nodeId) return;
        RaiseFlyingMaster(FlyingMasterSignal.SlaveBootupReceived, nodeId, null);
        if (!IsAssignedSlave(nodeId) || _bootHalted) return;
        // An expected reset response must first pass its own transmission confirmation and
        // freshness checks. Cancelling that worker here would restart a configuration restore.
        if (_slaveConfigurationBootUps[nodeId] is not null || _slaveNmtErrorResetting[nodeId]
            || _slaveBootResetPending[nodeId]) return;

        bool recovering = _slaveStarted[nodeId] || _slaveNmtErrorRecovering[nodeId];
        RestartSlaveBootChecks(nodeId, recovering);
        _slaveSeen[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.Announced);
        if (recovering)
        {
            _ = SlaveBootToken(nodeId);
            // A held master reset prevents all boot gates. Start the peer's budget when
            // ResumeHeldBoot can actually resume them, not while the announcement is held.
            if (!_coldResetPending) ArmPeerRecoveryDeadline(nodeId);
        }
        bool bootSlave = (Assignment(nodeId) & SlaveBootBit) != 0 && (ReadStartup() & NmtSuppressSlaveStartBit) == 0;
        if (!bootSlave) FinishNmtErrorRecovery(nodeId);
        // NoteSlaveNmtState records this actual boot-up and resumes the normal gates. During
        // a held cold reset it retains the announcement until ResumeHeldBoot can act on it.
    }
}
