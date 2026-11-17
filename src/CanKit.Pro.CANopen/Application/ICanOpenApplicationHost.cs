using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen.Application;

/// <summary>What the node gives an attached application: its dictionary, its clock and state, and the
/// ways to ask it to transmit, report an error or change state. The node implements it, one host
/// per application. The thread each member may be called on is given on the member.</summary>
internal interface ICanOpenApplicationHost
{
    /// <summary>The node's object dictionary. Any thread.</summary>
    ObjectDictionary ObjectDictionary { get; }

    /// <summary>The node's NMT state. Actor loop, or <see cref="ICanOpenApplication.Attach"/>.</summary>
    NmtState State { get; }

    /// <summary>The node's monotonic clock, the time source its deadlines run on. Any thread.</summary>
    TimeSpan Now { get; }

    /// <summary>Whether a communication failure is active. Actor loop.</summary>
    bool CommunicationFailureActive { get; }

    /// <summary>Arms a deadline on the node's clock; <paramref name="onExpired"/> runs on the actor loop.</summary>
    IDeadline Arm(TimeSpan delay, Action onExpired);

    /// <summary>Sets the device type (<c>1000h</c>) of a logical device. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType);

    /// <summary>Asks the node to transmit the TPDOs that map these entries. Actor loop.</summary>
    void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries);

    /// <summary>Raises an error: sets its bits in the error register and sends the EMCY. Actor loop.</summary>
    void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default);

    /// <summary>Ends an error raised with <see cref="RaiseError"/>. Actor loop.</summary>
    void ClearError(ushort errorCode);

    /// <summary>Asks the node to enter an NMT state. Actor loop.</summary>
    void RequestNmtState(NmtState target);
}
