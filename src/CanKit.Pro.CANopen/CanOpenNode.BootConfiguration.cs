using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly bool[] _slaveConfigurationVerified = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveConfigurationVerifying = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveConfigurationFailed = new bool[CanOpenCobId.MaxNodeId + 1];

    private bool IsMandatoryConfigurationPending(byte nodeId)
        => BootConfigurationEnabled && IsMandatory(nodeId)
           && (ReadStartup() & NmtSuppressSlaveStartBit) == 0
           && (Assignment(nodeId) & SlaveBootBit) != 0 && !_slaveConfigurationVerified[nodeId];

    private bool HasUnverifiedMandatoryConfiguration()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatoryConfigurationPending(id)) return true;
        return false;
    }

    private void BeginSlaveConfigurationVerification(byte nodeId)
    {
        _slaveConfigurationVerifying[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.VerifyingConfiguration);
        uint date = _od.ReadUnsigned(Co.ExpectedConfigurationDate, nodeId);
        uint time = _od.ReadUnsigned(Co.ExpectedConfigurationTime, nodeId);
        var token = SlaveBootToken(nodeId);
        int generation = _bootGeneration;
        var plan = _slaveBootConfigurations[nodeId];
        byte restore = _options.EnableBootConfigurationDownload && (Assignment(nodeId) & 0x80) != 0
            ? (byte)_od.ReadUnsigned(Co.RestoreConfiguration, nodeId) : (byte)0;
        _ = Task.Run(async () =>
        {
            var result = await VerifySlaveConfigurationAsync(nodeId, date, time, token).ConfigureAwait(false);
            if (plan is not null && (result.Status == SlaveBootStatus.ConfigurationRequired
                || (result.Status == SlaveBootStatus.ConfigurationVerified && plan.Incomplete)))
                result = await DownloadSlaveConfigurationAsync(nodeId, plan, restore, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            PostSlaveConfigurationResult(nodeId, generation, result);
        });
    }

    private void PostSlaveConfigurationResult(byte nodeId, int generation, SlaveBootResult result)
    {
        try { _actor.Post(() => OnSlaveConfigurationVerified(nodeId, generation, result)); }
        catch (ObjectDisposedException)
        {
            // Dispose can close the mailbox after the worker's cancellation check.
            return;
        }
    }

    private async Task<SlaveBootResult> VerifySlaveConfigurationAsync(byte nodeId, uint date, uint time, CancellationToken token)
    {
        // CiA 302-3 §3.7/3.8: either zero requests configuration, not a skipped comparison.
        // No SDOs or writes are needed to establish that a download would be required.
        if (date == 0 || time == 0)
            return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationRequired,
                Co.VerifyConfiguration, date == 0 ? (byte)1 : (byte)2, expectedValue: 0);

        byte subindex = 1;
        uint expected = date;
        var gate = PeerSdoChannel(nodeId);
        bool held = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            // One channel lease and one description snapshot cover both uploads.
            var binding = new PeerDescriptionBinding(BoundPeerDescription(nodeId) is { } description
                ? DescribedObjects.Capture(description) : null, Array.Empty<(ushort, byte)>());
            for (; subindex <= 2; subindex++)
            {
                token.ThrowIfCancellationRequested();
                expected = subindex == 1 ? date : time;
                var raw = await SdoUploadCoreAsync(nodeId, Co.VerifyConfiguration, subindex,
                    SdoTransferMode.Auto, token, transaction: binding).ConfigureAwait(false);
                if (raw.Length != 4) throw new InvalidDataException($"Configuration 0x1020:{subindex:X2} is not UNSIGNED32.");
                uint actual = (uint)raw[0] | ((uint)raw[1] << 8) | ((uint)raw[2] << 16) | ((uint)raw[3] << 24);
                if (actual != expected)
                    return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationRequired,
                        Co.VerifyConfiguration, subindex, expected, actual);
            }
            return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationVerified);
        }
        catch (Exception ex)
        {
            return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationReadFailed,
                Co.VerifyConfiguration, subindex, expected, error: ex);
        }
        finally
        {
            if (held) gate.Release();
        }
    }

    private void OnSlaveConfigurationVerified(byte nodeId, int generation, SlaveBootResult result)
    {
        if (generation != _bootGeneration || !_slaveConfigurationVerifying[nodeId]) return;
        _slaveConfigurationVerifying[nodeId] = false;
        if (_disposed != 0 || _flyingMasterRole != FlyingMasterRole.Active || _bootHalted) return;
        // An abandoned forced reset rechecks; a confirmed reset cancels this generation.
        if (_coldResetPending) return;
        _slaveBootResults[nodeId] = result;
        if (result.Status is SlaveBootStatus.ConfigurationVerified or SlaveBootStatus.ConfigurationDownloaded)
        {
            _slaveConfigurationVerified[nodeId] = true;
            ConsiderStart(nodeId, SlaveStateForBoot(nodeId));
            TryFinishBoot();
            return;
        }
        _slaveConfigurationFailed[nodeId] = true;
        if (ActiveSlaveBootEnabled && !IsMandatory(nodeId))
        {
            ScheduleActiveSlaveRetry(nodeId, FlyingMasterSignal.SlaveConfigurationInvalid);
            return;
        }
        uint startup = ReadStartup();
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (IsMandatory(nodeId) || simultaneous)
            ApplyBootErrorReaction(new[] { nodeId }, FlyingMasterSignal.SlaveConfigurationInvalid);
        else
        {
            RaiseFlyingMaster(FlyingMasterSignal.SlaveConfigurationInvalid, nodeId, null);
            TryFinishBoot();
        }
    }
}
