using System;

namespace CanKit.Pro.CANopen.Nmt;

/// <summary>The latest step or outcome of an assigned slave in the current boot.</summary>
public enum SlaveBootStatus
{
    /// <summary>No announcement has been received in this boot.</summary>
    WaitingForAnnouncement,
    /// <summary>The slave announced itself; automatic Start may be disabled.</summary>
    Announced,
    /// <summary>The required device-type upload or configured identity is being read over SDO.</summary>
    VerifyingIdentity,
    /// <summary>The device-type upload, when required, succeeded and all nonzero expectations matched.</summary>
    IdentityVerified,
    /// <summary>NMT Start was queued; this does not confirm Operational on the slave.</summary>
    StartRequested,
    /// <summary>The slave announced Operational and needs no Start.</summary>
    AlreadyOperational,
    /// <summary>A value failed its nonzero expectation, including revision compatibility.</summary>
    IdentityMismatch,
    /// <summary>An identity upload failed or returned a value other than UNSIGNED32.</summary>
    IdentityReadFailed,
    /// <summary>The mandatory slave did not finish before the boot deadline.</summary>
    BootTimeout,
    /// <summary>The bound safety configuration did not verify before Start.</summary>
    SafetyConfigurationInvalid,
    /// <summary>The active boot's Reset Communication send was not confirmed; a retry is scheduled.</summary>
    ResetCommunicationFailed,
    /// <summary>Configuration date/time is being verified or the registered configuration is being applied.</summary>
    VerifyingConfiguration,
    /// <summary>Both nonzero configuration expectations matched 1020h:01/:02.</summary>
    ConfigurationVerified,
    /// <summary>A zero expectation or a mismatching configuration requires configuration work; Start is blocked.</summary>
    ConfigurationRequired,
    /// <summary>A configuration upload failed or was not UNSIGNED32.</summary>
    ConfigurationReadFailed,
    /// <summary>The registered configuration stream was acknowledged in full.</summary>
    ConfigurationDownloaded,
    /// <summary>Preflight, a configuration write, restore or its reset/boot-up failed.</summary>
    ConfigurationDownloadFailed,
    /// <summary>The error-control startup stage is waiting for a fresh heartbeat.</summary>
    WaitingForErrorControl,
    /// <summary>A heartbeat was received in time, guarding was started, or neither was configured.</summary>
    ErrorControlReady,
    /// <summary>No valid heartbeat was received within the error-control startup window.</summary>
    ErrorControlFailed,
}

/// <summary>An immutable snapshot of one slave's boot progress. A new boot replaces the results.</summary>
public sealed class SlaveBootResult
{
    internal SlaveBootResult(byte nodeId, SlaveBootStatus status, ushort? index = null,
        byte? subindex = null, uint? expectedValue = null, uint? actualValue = null, Exception? error = null)
    {
        NodeId = nodeId;
        Status = status;
        Index = index;
        Subindex = subindex;
        ExpectedValue = expectedValue;
        ActualValue = actualValue;
        Error = error;
    }

    /// <summary>The assigned slave's node-id.</summary>
    public byte NodeId { get; }
    /// <summary>The latest step or outcome; StartRequested is not a remote acknowledgement.</summary>
    public SlaveBootStatus Status { get; }
    /// <summary>The object associated with the failed step, or null. Error-control startup
    /// refers to the local consumer-heartbeat object (1016h).</summary>
    public ushort? Index { get; }
    /// <summary>The remote sub-index that failed verification, or null.</summary>
    public byte? Subindex { get; }
    /// <summary>The configured expectation, or null. ConfigurationRequired may carry zero,
    /// which requests configuration rather than bypassing its check.</summary>
    public uint? ExpectedValue { get; }
    /// <summary>The received UNSIGNED32 for a mismatch, or null.</summary>
    public uint? ActualValue { get; }
    /// <summary>The verification or configuration update failure, including an SDO abort when supplied by the peer, or null.</summary>
    public Exception? Error { get; }
}
