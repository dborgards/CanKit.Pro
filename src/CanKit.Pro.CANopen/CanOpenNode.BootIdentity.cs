using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly bool[] _slaveIdentityVerified = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveIdentityVerifying = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveIdentityFailed = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly SlaveBootResult?[] _slaveBootResults = new SlaveBootResult?[CanOpenCobId.MaxNodeId + 1];

    internal IReadOnlyList<SlaveBootResult> GetSlaveBootResults()
    {
        ThrowIfDisposed();
        return _actor.IsOnCurrentActor ? CaptureSlaveBootResults()
            : _actor.PostAsync(CaptureSlaveBootResults).GetAwaiter().GetResult();
    }

    private IReadOnlyList<SlaveBootResult> CaptureSlaveBootResults()
        => Array.AsReadOnly(_slaveBootResults.OfType<SlaveBootResult>().ToArray());

    private bool RequiresSlaveIdentityVerification(byte nodeId)
    {
        if (_options.RequireBootDeviceTypeUpload || ActiveSlaveBootEnabled || _options.EnableBootConfigurationDownload) return true;
        for (ushort index = Co.ExpectedDeviceType; index <= Co.ExpectedSerialNumber; index++)
            if (_od.ReadUnsigned(index, nodeId) != 0) return true;
        return false;
    }

    // A Start to node 0 would reach failed or unseen slaves too. As with safety slaves, keep
    // the simultaneous start moment but address each announced, verified slave individually.
    private bool HasAssignedIdentityVerification()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsAssignedSlave(id) && RequiresSlaveIdentityVerification(id)) return true;
        return false;
    }

    private bool HasUnverifiedMandatoryIdentity()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatoryIdentityPending(id)) return true;
        return false;
    }

    private bool IsMandatoryIdentityPending(byte nodeId)
        => (ReadStartup() & NmtSuppressSlaveStartBit) == 0 && IsMandatory(nodeId)
           && (Assignment(nodeId) & SlaveBootBit) != 0 && RequiresSlaveIdentityVerification(nodeId)
           && !_slaveIdentityVerified[nodeId];

    private void BeginSlaveIdentityVerification(byte nodeId)
    {
        _slaveIdentityVerifying[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.VerifyingIdentity);
        var expectations = new uint[5];
        for (int i = 0; i < expectations.Length; i++)
            expectations[i] = _od.ReadUnsigned((ushort)(Co.ExpectedDeviceType + i), nodeId);
        var token = SlaveBootToken(nodeId);
        int generation = _bootGeneration;
        _ = Task.Run(async () =>
        {
            var result = await VerifySlaveIdentityAsync(nodeId, expectations, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            PostSlaveIdentityResult(nodeId, generation, result, token);
        });
    }

    private void PostSlaveIdentityResult(byte nodeId, int generation, SlaveBootResult result, CancellationToken token)
    {
        try { _actor.Post(() => { if (!token.IsCancellationRequested) OnSlaveIdentityVerified(nodeId, generation, result); }); }
        catch (ObjectDisposedException)
        {
            // Dispose can close the mailbox after the worker's cancellation check. There is
            // no boot left to update and no live actor on which to report a boot failure.
            return;
        }
    }

    private async Task<SlaveBootResult> VerifySlaveIdentityAsync(byte nodeId, uint[] expectations, CancellationToken token)
    {
        ushort index = Co.DeviceType;
        byte subindex = 0;
        uint? expected = null;
        var gate = PeerSdoChannel(nodeId);
        bool held = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            // Pin the bound peer description for the transaction, including the base objects.
            var binding = new PeerDescriptionBinding(BoundPeerDescription(nodeId) is { } description
                ? DescribedObjects.Capture(description) : null, Array.Empty<(ushort, byte)>());
            for (int i = 0; i < expectations.Length; i++)
            {
                // A zero 1F84h disables the comparison, but the opted-in reachability
                // upload still precedes 1018h (CiA 302-2 §3.4 c-e).
                if (expectations[i] == 0 && (i != 0 || !(_options.RequireBootDeviceTypeUpload || ActiveSlaveBootEnabled || _options.EnableBootConfigurationDownload))) continue;
                token.ThrowIfCancellationRequested();
                index = i == 0 ? Co.DeviceType : Co.Identity;
                subindex = (byte)i;
                expected = expectations[i] == 0 ? null : expectations[i];
                var raw = await SdoUploadCoreAsync(nodeId, index, subindex, SdoTransferMode.Auto, token,
                    transaction: binding).ConfigureAwait(false);
                if (raw.Length != 4) throw new InvalidDataException($"Identity 0x{index:X4}:{subindex:X2} is not UNSIGNED32.");
                uint actual = (uint)raw[0] | ((uint)raw[1] << 8) | ((uint)raw[2] << 16) | ((uint)raw[3] << 24);
                // CiA 302-2 §6.9 permits a newer minor revision within the same major.
                bool matches = expected is null || (i == 3
                    ? (actual >> 16) == (expectations[i] >> 16) && (actual & 0xFFFF) >= (expectations[i] & 0xFFFF)
                    : actual == expectations[i]);
                if (!matches)
                    return new SlaveBootResult(nodeId, SlaveBootStatus.IdentityMismatch, index, subindex, expected, actual);
            }
            return new SlaveBootResult(nodeId, SlaveBootStatus.IdentityVerified);
        }
        catch (Exception ex)
        {
            // This is the worker's exception-to-result boundary, including transport faults
            // and malformed bound descriptions. Keep the original exception for the caller;
            // a faulted, unobserved worker task would otherwise leave the slave verifying.
            return new SlaveBootResult(nodeId, SlaveBootStatus.IdentityReadFailed, index, subindex, expected, error: ex);
        }
        finally
        {
            if (held) gate.Release();
        }
    }

    private void OnSlaveIdentityVerified(byte nodeId, int generation, SlaveBootResult result)
    {
        if (generation != _bootGeneration || !_slaveIdentityVerifying[nodeId]) return;
        _slaveIdentityVerifying[nodeId] = false;
        if (_disposed != 0 || _flyingMasterRole != FlyingMasterRole.Active || _bootHalted) return;
        // Recheck after an abandoned forced reset; a confirmed reset cancels this generation.
        if (_coldResetPending) return;
        _slaveBootResults[nodeId] = result;
        if (result.Status == SlaveBootStatus.IdentityVerified)
        {
            _slaveIdentityVerified[nodeId] = true;
            ConsiderStart(nodeId, SlaveStateForBoot(nodeId));
            TryFinishBoot();
            return;
        }
        _slaveIdentityFailed[nodeId] = true;
        if (ActiveSlaveBootEnabled && (!IsMandatory(nodeId) || IsDeviceTypeNoResponse(result)))
        {
            ScheduleActiveSlaveRetry(nodeId, FlyingMasterSignal.SlaveIdentityInvalid);
            return;
        }
        uint startup = ReadStartup();
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (IsMandatory(nodeId) || simultaneous)
            ApplyBootErrorReaction(new[] { nodeId }, FlyingMasterSignal.SlaveIdentityInvalid);
        else
        {
            RaiseFlyingMaster(FlyingMasterSignal.SlaveIdentityInvalid, nodeId, null);
            TryFinishBoot();
        }
    }
}
