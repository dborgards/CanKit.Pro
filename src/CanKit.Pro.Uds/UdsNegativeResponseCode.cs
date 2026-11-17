namespace CanKit.Pro.Uds;

/// <summary>
/// Negative Response Codes, one member for each code ISO 14229-1:2026 Table A.1 names. The
/// ranges the table reserves (for future definition, for specific conditions not correct, or
/// for the vehicle manufacturer) have no member; such a value is still delivered as its raw
/// <see cref="byte"/> on <see cref="UdsNegativeResponseException.Code"/>, with a
/// <see cref="UdsNegativeResponseException.CodeAsEnum"/> of <c>null</c>. The summaries
/// paraphrase the codes' meaning; the standard's table is the reference.
/// </summary>
public enum UdsNegativeResponseCode : byte
{
    /// <summary>generalReject (0x10).</summary>
    GeneralReject = 0x10,

    /// <summary>serviceNotSupported (0x11).</summary>
    ServiceNotSupported = 0x11,

    /// <summary>subFunctionNotSupported (0x12).</summary>
    SubFunctionNotSupported = 0x12,

    /// <summary>incorrectMessageLengthOrInvalidFormat (0x13).</summary>
    IncorrectMessageLengthOrInvalidFormat = 0x13,

    /// <summary>responseTooLong (0x14): the response would exceed what the transport can carry.</summary>
    ResponseTooLong = 0x14,

    /// <summary>busyRepeatRequest (0x21).</summary>
    BusyRepeatRequest = 0x21,

    /// <summary>conditionsNotCorrect (0x22).</summary>
    ConditionsNotCorrect = 0x22,

    /// <summary>requestSequenceError (0x24).</summary>
    RequestSequenceError = 0x24,

    /// <summary>noResponseFromSubnetComponent (0x25): a gateway got no answer from the component it forwarded the request to.</summary>
    NoResponseFromSubnetComponent = 0x25,

    /// <summary>failurePreventsExecutionOfRequestedAction (0x26): a fault in the server stops the requested action.</summary>
    FailurePreventsExecutionOfRequestedAction = 0x26,

    /// <summary>requestOutOfRange (0x31).</summary>
    RequestOutOfRange = 0x31,

    /// <summary>securityAccessDenied (0x33).</summary>
    SecurityAccessDenied = 0x33,

    /// <summary>authenticationRequired (0x34): the client must authenticate before the request is allowed.</summary>
    AuthenticationRequired = 0x34,

    /// <summary>invalidKey (0x35).</summary>
    InvalidKey = 0x35,

    /// <summary>exceedNumberOfAttempts (0x36).</summary>
    ExceedNumberOfAttempts = 0x36,

    /// <summary>requiredTimeDelayNotExpired (0x37).</summary>
    RequiredTimeDelayNotExpired = 0x37,

    /// <summary>secureDataTransmissionRequired (0x38): the request must be sent through secured data transmission.</summary>
    SecureDataTransmissionRequired = 0x38,

    /// <summary>secureDataTransmissionNotAllowed (0x39): the request must not be sent through secured data transmission.</summary>
    SecureDataTransmissionNotAllowed = 0x39,

    /// <summary>secureDataVerificationFailed (0x3A): the server could not verify the secured message.</summary>
    SecureDataVerificationFailed = 0x3A,

    /// <summary>Certificate verification failed, invalid time period (0x50).</summary>
    CertificateVerificationFailedInvalidTimePeriod = 0x50,

    /// <summary>Certificate verification failed, invalid signature (0x51).</summary>
    CertificateVerificationFailedInvalidSignature = 0x51,

    /// <summary>Certificate verification failed, invalid chain of trust (0x52).</summary>
    CertificateVerificationFailedInvalidChainOfTrust = 0x52,

    /// <summary>Certificate verification failed, invalid type (0x53).</summary>
    CertificateVerificationFailedInvalidType = 0x53,

    /// <summary>Certificate verification failed, invalid format (0x54).</summary>
    CertificateVerificationFailedInvalidFormat = 0x54,

    /// <summary>Certificate verification failed, invalid content (0x55).</summary>
    CertificateVerificationFailedInvalidContent = 0x55,

    /// <summary>Certificate verification failed, invalid scope (0x56).</summary>
    CertificateVerificationFailedInvalidScope = 0x56,

    /// <summary>Certificate verification failed, the certificate is revoked (0x57).</summary>
    CertificateVerificationFailedInvalidCertificate = 0x57,

    /// <summary>Ownership verification failed (0x58).</summary>
    OwnershipVerificationFailed = 0x58,

    /// <summary>Challenge calculation failed (0x59).</summary>
    ChallengeCalculationFailed = 0x59,

    /// <summary>Setting the access rights failed (0x5A).</summary>
    SettingAccessRightsFailed = 0x5A,

    /// <summary>Creating or deriving the session key failed (0x5B).</summary>
    SessionKeyCreationDerivationFailed = 0x5B,

    /// <summary>Using the configuration data failed (0x5C).</summary>
    ConfigurationDataUsageFailed = 0x5C,

    /// <summary>De-authentication failed (0x5D).</summary>
    DeAuthenticationFailed = 0x5D,

    /// <summary>uploadDownloadNotAccepted (0x70).</summary>
    UploadDownloadNotAccepted = 0x70,

    /// <summary>transferDataSuspended (0x71).</summary>
    TransferDataSuspended = 0x71,

    /// <summary>generalProgrammingFailure (0x72).</summary>
    GeneralProgrammingFailure = 0x72,

    /// <summary>wrongBlockSequenceCounter (0x73).</summary>
    WrongBlockSequenceCounter = 0x73,

    /// <summary>
    /// requestCorrectlyReceived-ResponsePending (0x78). Server acknowledges the request but needs
    /// more time; the client MUST restart its P2* timer and keep waiting until the final response
    /// arrives or the P2* budget is exhausted (SRS FR-UDS-009, ISO 14229-1:2026 Table A.1 NRC 78h; P2* per ISO 14229-2).
    /// </summary>
    RequestCorrectlyReceivedResponsePending = 0x78,

    /// <summary>subFunctionNotSupportedInActiveSession (0x7E).</summary>
    SubFunctionNotSupportedInActiveSession = 0x7E,

    /// <summary>serviceNotSupportedInActiveSession (0x7F).</summary>
    ServiceNotSupportedInActiveSession = 0x7F,

    /// <summary>rpmTooHigh (0x81): engine speed above the limit the action needs.</summary>
    RpmTooHigh = 0x81,

    /// <summary>rpmTooLow (0x82): engine speed below the limit the action needs.</summary>
    RpmTooLow = 0x82,

    /// <summary>engineIsRunning (0x83): the action needs the engine stopped.</summary>
    EngineIsRunning = 0x83,

    /// <summary>engineIsNotRunning (0x84): the action needs the engine running.</summary>
    EngineIsNotRunning = 0x84,

    /// <summary>engineRunTimeTooLow (0x85): the engine has not run long enough for the action.</summary>
    EngineRunTimeTooLow = 0x85,

    /// <summary>temperatureTooHigh (0x86): a temperature is above the limit the action needs.</summary>
    TemperatureTooHigh = 0x86,

    /// <summary>temperatureTooLow (0x87): a temperature is below the limit the action needs.</summary>
    TemperatureTooLow = 0x87,

    /// <summary>vehicleSpeedTooHigh (0x88): vehicle speed above the limit the action needs.</summary>
    VehicleSpeedTooHigh = 0x88,

    /// <summary>vehicleSpeedTooLow (0x89): vehicle speed below the limit the action needs.</summary>
    VehicleSpeedTooLow = 0x89,

    /// <summary>throttle/pedalTooHigh (0x8A): throttle or pedal position above the limit the action needs.</summary>
    ThrottlePedalTooHigh = 0x8A,

    /// <summary>throttle/pedalTooLow (0x8B): throttle or pedal position below the limit the action needs.</summary>
    ThrottlePedalTooLow = 0x8B,

    /// <summary>transmissionRangeNotInNeutral (0x8C): the action needs the transmission in neutral.</summary>
    TransmissionRangeNotInNeutral = 0x8C,

    /// <summary>transmissionRangeNotInGear (0x8D): the action needs a gear engaged.</summary>
    TransmissionRangeNotInGear = 0x8D,

    /// <summary>brakeSwitch(es)NotClosed (0x8F): the action needs the brake applied.</summary>
    BrakeSwitchesNotClosed = 0x8F,

    /// <summary>shifterLeverNotInPark (0x90): the action needs the shifter in park.</summary>
    ShifterLeverNotInPark = 0x90,

    /// <summary>torqueConverterClutchLocked (0x91): the action needs the torque converter clutch unlocked.</summary>
    TorqueConverterClutchLocked = 0x91,

    /// <summary>voltageTooHigh (0x92): supply voltage above the limit the action needs.</summary>
    VoltageTooHigh = 0x92,

    /// <summary>voltageTooLow (0x93): supply voltage below the limit the action needs.</summary>
    VoltageTooLow = 0x93,

    /// <summary>resourceTemporarilyNotAvailable (0x94): a resource the request needs is busy for now.</summary>
    ResourceTemporarilyNotAvailable = 0x94,
}
