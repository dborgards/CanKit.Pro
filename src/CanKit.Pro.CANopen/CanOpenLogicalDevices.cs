using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// One logical device of a CANopen node: its position among the node's up to eight logical
/// devices and the device-type word that describes it.
/// </summary>
public sealed class CanOpenLogicalDevice
{
    /// <summary>Creates the description of one logical device.</summary>
    /// <param name="index">The position of the logical device, <c>0</c> to <c>7</c>.</param>
    /// <param name="deviceType">The device-type word of this logical device.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public CanOpenLogicalDevice(int index, CanOpenDeviceType deviceType)
    {
        // The range is defined once, in Validate; the constructor names its own parameter.
        CanOpenLogicalDevices.Validate(index, nameof(index));
        ObjectAreaStart = CanOpenLogicalDevices.ObjectAreaStart(index);
        Index = index;
        DeviceType = deviceType;
    }

    /// <summary>The position of this logical device, <c>0</c> to <c>7</c>.</summary>
    public int Index { get; }

    /// <summary>The device-type word of this logical device.</summary>
    public CanOpenDeviceType DeviceType { get; }

    /// <summary>The first object index of this logical device's profile area, see
    /// <see cref="CanOpenLogicalDevices.ObjectAreaStart(int)"/>.</summary>
    public ushort ObjectAreaStart { get; }

    /// <summary>The number to add to a profile's PDO numbers for this logical device, see
    /// <see cref="CanOpenLogicalDevices.PdoNumberOffset(int)"/>.</summary>
    public int PdoNumberOffset => CanOpenLogicalDevices.PdoNumberOffset(Index);
}

/// <summary>How reading the logical-device chain of a peer ended.</summary>
public enum CanOpenLogicalDeviceReadEnd
{
    /// <summary>The peer itself ended the chain, consistently with <c>1000h</c>: <c>1000h</c> does
    /// not carry <c>FFFFh</c>; or it does, at least two logical devices were read, the first of the
    /// profile <c>1000h</c> names, and the peer aborted the next device-type object with "object
    /// does not exist"; or eight devices were read. Every logical device the peer has is in the
    /// list. The table in the remarks of <see cref="CanOpenLogicalDevices.ReadAsync"/> lists every
    /// way a read ends.</summary>
    Complete = 0,

    /// <summary><c>1000h</c> says more logical devices follow, but the SDO client refused to read
    /// <c>67FFh</c> because no peer description is bound for the node. Bind one with
    /// <see cref="ICanOpenNode.BindPeerDeviceDescription"/> and read again.</summary>
    PeerDescriptionRequired = 1,

    /// <summary>A read inside the chain ended in an SDO abort other than the peer's "object does
    /// not exist", the client's own timeout included, or returned a value that is not four bytes;
    /// or the peer's answers contradict each other: <c>67FFh</c> names another profile than
    /// <c>1000h</c>, or <c>1000h</c> announces several logical devices and the peer reports
    /// <c>67FFh</c> or <c>6FFFh</c> as non-existent. <see cref="CanOpenLogicalDeviceReadResult.Failure"/>
    /// says which (its remarks tell the cases apart), and the list holds the devices read until
    /// then, a <c>67FFh</c> that contradicts <c>1000h</c> included.</summary>
    Failed = 2,

    /// <summary>The bound peer description does not declare the next device-type object, so the
    /// SDO client refused to ask for it. The description is the list of objects the client may
    /// touch on that peer: the list is complete as far as the bound description goes, not
    /// necessarily as far as the peer goes. When <c>1000h</c> announced further devices but the
    /// description declares no <c>67FFh</c>, the only device is the copy of <c>1000h</c>.</summary>
    EndedByPeerDescription = 3,
}

/// <summary>The logical devices of a peer as <see cref="CanOpenLogicalDevices.ReadAsync"/> found
/// them, and how the reading ended.</summary>
public sealed class CanOpenLogicalDeviceReadResult
{
    internal CanOpenLogicalDeviceReadResult(CanOpenDeviceType deviceType,
        List<CanOpenLogicalDevice> devices, CanOpenLogicalDeviceReadEnd end, Exception? failure)
    {
        DeviceType = deviceType;
        Devices = devices.ToArray();
        End = end;
        Failure = failure;
    }

    /// <summary>The device-type word of object <c>1000h</c> as the peer returned it.</summary>
    public CanOpenDeviceType DeviceType { get; }

    /// <summary>The logical devices read, ordered by <see cref="CanOpenLogicalDevice.Index"/>.
    /// Never empty: a node without further logical devices is one logical device, and the first
    /// device is reported with the type of <c>1000h</c> when <c>67FFh</c> could not be read. Every
    /// word is the one the peer returned: when <c>67FFh</c> names another profile than
    /// <c>1000h</c>, the first device carries the word of <c>67FFh</c> and
    /// <see cref="DeviceType"/> the word of <c>1000h</c>, and the reading
    /// <see cref="CanOpenLogicalDeviceReadEnd.Failed"/>.</summary>
    public IReadOnlyList<CanOpenLogicalDevice> Devices { get; }

    /// <summary>How the reading ended.</summary>
    public CanOpenLogicalDeviceReadEnd End { get; }

    /// <summary>The failure that ended the reading. Not <see langword="null"/> exactly when
    /// <see cref="End"/> is <see cref="CanOpenLogicalDeviceReadEnd.Failed"/>.</summary>
    /// <remarks>
    /// What it is tells the failures apart:
    /// <list type="bullet">
    /// <item><description>an <see cref="SdoAbortException"/>: a read inside the chain was aborted,
    /// by the peer with a code other than "object does not exist", or locally, the client's own
    /// timeout included;</description></item>
    /// <item><description>an <see cref="InvalidDataException"/> whose
    /// <see cref="Exception.InnerException"/> is the peer's <see cref="SdoAbortException"/>
    /// "object does not exist": <c>1000h</c> announces several logical devices and the peer ended
    /// the chain before the second;</description></item>
    /// <item><description>an <see cref="InvalidDataException"/> without an inner exception, where the
    /// profile number of the first of <see cref="Devices"/> differs from the one of
    /// <see cref="DeviceType"/>: <c>67FFh</c> names another profile than <c>1000h</c>;</description></item>
    /// <item><description>an <see cref="InvalidDataException"/> without an inner exception, where the
    /// two profile numbers agree: a device-type object did not return four bytes.</description></item>
    /// </list>
    /// </remarks>
    public Exception? Failure { get; }
}

/// <summary>
/// The arithmetic that places a logical device in the object dictionary. A node holds up to
/// <see cref="MaxCount"/> logical devices. Logical device <c>n</c> owns the <c>800h</c> objects
/// from <c>6000h + n·800h</c>, so a profile that defines an object at <c>6xxxh</c> finds it
/// <c>n·800h</c> higher, and its PDO numbers are <c>n·64</c> higher.
/// </summary>
public static class CanOpenLogicalDevices
{
    /// <summary>The number of logical devices a node can hold.</summary>
    public const int MaxCount = 8;

    private const ushort DeviceTypeObject = 0x1000;
    internal const ushort FirstArea = 0x6000;
    internal const int AreaSize = 0x800;
    internal const int PdosPerLogicalDevice = 64;

    /// <summary>The first object index of a logical device, <c>6000h + logicalDevice·800h</c>.</summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static ushort ObjectAreaStart(int logicalDevice)
    {
        Validate(logicalDevice);
        return (ushort)(FirstArea + (logicalDevice * AreaSize));
    }

    /// <summary>The index of the object that holds a logical device's device type,
    /// <c>67FFh + logicalDevice·800h</c>.</summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static ushort DeviceTypeIndex(int logicalDevice)
    {
        Validate(logicalDevice);
        return (ushort)(FirstArea + AreaSize - 1 + (logicalDevice * AreaSize));
    }

    /// <summary>The offset of a logical device's PDO numbers, <c>logicalDevice·64</c>.</summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static int PdoNumberOffset(int logicalDevice)
    {
        Validate(logicalDevice);
        return logicalDevice * PdosPerLogicalDevice;
    }

    /// <summary>Moves an object index that a profile defines in <c>6000h</c> to <c>67FFh</c> to
    /// where the given logical device holds it.</summary>
    /// <param name="logicalDevice">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <param name="profileIndex">The object index as the profile defines it.</param>
    /// <returns><paramref name="profileIndex"/> plus <c>logicalDevice·800h</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>, or <paramref name="profileIndex"/> is outside <c>6000h</c> to
    /// <c>67FFh</c>.</exception>
    public static ushort ToAbsoluteIndex(int logicalDevice, ushort profileIndex)
    {
        Validate(logicalDevice);
        if (profileIndex < FirstArea || profileIndex >= FirstArea + AreaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(profileIndex), profileIndex,
                "A profile object index lies in 6000h to 67FFh.");
        }

        return (ushort)(profileIndex + (logicalDevice * AreaSize));
    }

    /// <summary>
    /// Reads the logical devices of a peer: <c>1000h</c>, and when its additional information is
    /// <c>FFFFh</c>, the device-type objects <c>67FFh</c>, <c>6FFFh</c> and on up to <c>9FFFh</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A peer without <c>FFFFh</c> is one logical device. With it, the chain is read until the
    /// peer reports a device-type object as non-existent, until eight devices are read, or until
    /// one of the cases below ends it earlier.
    /// </para>
    /// <para>
    /// <c>FFFFh</c> says the node is a module of several logical devices, and <c>1000h</c> names
    /// the profile of the first of them, whose device type is <c>67FFh</c> (CiA 301 v4.2.0
    /// §7.5.2.1; CiA 401-1 v3.1.0 §6.2.1). Answers that break either rule contradict each other:
    /// the read ends as <see cref="CanOpenLogicalDeviceReadEnd.Failed"/> with an
    /// <see cref="InvalidDataException"/>, keeps every word as the peer returned it, and asks
    /// nothing further. Only the profile numbers are compared: the additional information of
    /// device 0 is its own and lives in <c>67FFh</c>. Profile number <c>0000h</c>, a device that
    /// follows no standardized profile, is compared like any other: a <c>1000h</c> of profile
    /// <c>0000h</c> over a <c>67FFh</c> of profile 401 contradicts it. That is a reading of §7.5.2.1,
    /// which names "the device profile of the first device" in <c>1000h</c> without exempting
    /// <c>0000h</c>. A refusal by the SDO client's gate is not an
    /// answer of the peer and contradicts nothing. Every way a read ends:
    /// </para>
    /// <list type="table">
    /// <listheader><term>The peer's answers</term><description>The result</description></listheader>
    /// <item><term><c>1000h</c> without <c>FFFFh</c></term><description><see cref="CanOpenLogicalDeviceReadEnd.Complete"/>,
    /// one device, the copy of <c>1000h</c>. Nothing else is asked, so a device-type object the peer
    /// may hold anyway is not seen.</description></item>
    /// <item><term><c>FFFFh</c>; <c>67FFh</c> of the profile of <c>1000h</c>, <c>6FFFh</c> and on;
    /// then "object does not exist" (<c>0602 0000h</c>) from the peer, or eight devices</term>
    /// <description><see cref="CanOpenLogicalDeviceReadEnd.Complete"/>, the devices read.</description></item>
    /// <item><term><c>FFFFh</c>; <c>0602 0000h</c> from the peer at <c>67FFh</c></term>
    /// <description><see cref="CanOpenLogicalDeviceReadEnd.Failed"/>, one device, the copy of
    /// <c>1000h</c>; the failure is an <see cref="InvalidDataException"/> whose inner exception is
    /// the peer's abort.</description></item>
    /// <item><term><c>FFFFh</c>; <c>67FFh</c> of the profile of <c>1000h</c>; <c>0602 0000h</c>
    /// from the peer at <c>6FFFh</c></term><description><see cref="CanOpenLogicalDeviceReadEnd.Failed"/>,
    /// one device, read from <c>67FFh</c>; the failure is an <see cref="InvalidDataException"/> whose
    /// inner exception is the peer's abort.</description></item>
    /// <item><term><c>FFFFh</c>; <c>67FFh</c> of another profile than <c>1000h</c></term>
    /// <description><see cref="CanOpenLogicalDeviceReadEnd.Failed"/> without asking <c>6FFFh</c>,
    /// one device, read from <c>67FFh</c>, beside <see cref="CanOpenLogicalDeviceReadResult.DeviceType"/>
    /// from <c>1000h</c>; the failure is an <see cref="InvalidDataException"/> naming both
    /// words.</description></item>
    /// <item><term><c>FFFFh</c>; the gate refuses a device-type object, no description
    /// bound</term><description><see cref="CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired"/>, the
    /// copy of <c>1000h</c>.</description></item>
    /// <item><term><c>FFFFh</c>; the gate refuses a device-type object the bound description does not
    /// declare</term><description><see cref="CanOpenLogicalDeviceReadEnd.EndedByPeerDescription"/>, the
    /// devices read before it, or the copy of <c>1000h</c> when that object is <c>67FFh</c>; one device
    /// read is not a contradiction here, since the peer was not asked.</description></item>
    /// <item><term><c>FFFFh</c>; inside the chain another SDO abort, a local abort with any code, or
    /// the client's own timeout</term><description><see cref="CanOpenLogicalDeviceReadEnd.Failed"/>, the
    /// devices read before it, or the copy of <c>1000h</c>; the failure is the
    /// <see cref="SdoAbortException"/>.</description></item>
    /// <item><term><c>FFFFh</c>; inside the chain a value that is not four bytes</term>
    /// <description><see cref="CanOpenLogicalDeviceReadEnd.Failed"/>, the devices read before it, or
    /// the copy of <c>1000h</c>; the failure is an <see cref="InvalidDataException"/> without an
    /// inner exception.</description></item>
    /// </list>
    /// <para>
    /// <c>67FFh</c> is not one of the objects the SDO client may read without a peer description.
    /// With none bound the result says <see cref="CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired"/>
    /// instead of throwing. With one bound, a device-type object the file does not declare ends the
    /// chain as <see cref="CanOpenLogicalDeviceReadEnd.EndedByPeerDescription"/>: the description
    /// is the list of objects the client may touch on that peer, so the list is complete as far as
    /// the description goes. Neither case throws; both concern <c>67FFh</c> and the objects after it.
    /// </para>
    /// <para>
    /// A failure of <c>1000h</c> propagates, a bound description that does not declare
    /// <c>1000h</c> (<see cref="PeerSdoAccessException"/>) included, as does cancellation. Inside
    /// the chain, what the table does not list, a transport fault
    /// (<see cref="CanOpenTransportException"/>) among it, reaches the caller as its exception,
    /// as it does for <c>1000h</c>.
    /// </para>
    /// </remarks>
    /// <param name="client">An opened node whose SDO client does the reads.</param>
    /// <param name="peerNodeId">The node-id of the peer, <c>1</c> to <c>127</c>.</param>
    /// <param name="cancellationToken">Cancels the outstanding request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside
    /// <c>1</c> to <c>127</c>.</exception>
    /// <exception cref="PeerSdoAccessException">A peer description is bound for the peer and does
    /// not declare <c>1000h</c>. The same refusal of a later object does not throw, see the
    /// remarks.</exception>
    /// <exception cref="SdoAbortException">Reading <c>1000h</c> ended in an SDO abort, the
    /// client's own timeout included.</exception>
    /// <exception cref="InvalidDataException"><c>1000h</c> did not return four bytes.</exception>
    /// <exception cref="CanOpenTransportException">A request did not reach the bus, for
    /// <c>1000h</c> or inside the chain.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled.</exception>
    /// <exception cref="InvalidOperationException">An SDO transfer with that peer is already in
    /// flight.</exception>
    public static async Task<CanOpenLogicalDeviceReadResult> ReadAsync(
        ICanOpenNode client, byte peerNodeId, CancellationToken cancellationToken = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        return await ReadCoreAsync(
            (index, token) => client.SdoUploadAsync(peerNodeId, index, 0x00, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The read. <paramref name="uploadSubindexZero"/> reads sub-index 0 of one object of the
    /// peer; the public entry point passes the client's SDO upload, a test passes its own.
    /// </summary>
    internal static async Task<CanOpenLogicalDeviceReadResult> ReadCoreAsync(
        Func<ushort, CancellationToken, Task<byte[]>> uploadSubindexZero, CancellationToken cancellationToken)
    {
        var first = await ReadDeviceTypeAsync(uploadSubindexZero, DeviceTypeObject, cancellationToken)
            .ConfigureAwait(false);
        var devices = new List<CanOpenLogicalDevice>();
        var end = CanOpenLogicalDeviceReadEnd.Complete;
        Exception? failure = null;

        if (first.HasFurtherLogicalDevices)
        {
            for (var logicalDevice = 0; logicalDevice < MaxCount; logicalDevice++)
            {
                try
                {
                    var type = await ReadDeviceTypeAsync(uploadSubindexZero, DeviceTypeIndex(logicalDevice), cancellationToken)
                        .ConfigureAwait(false);
                    // Kept as read, beside 1000h as read: the result shows both words and picks neither.
                    devices.Add(new CanOpenLogicalDevice(logicalDevice, type));
                    // 1000h names the profile of the first logical device (CiA 301 v4.2.0 §7.5.2.1);
                    // its additional information is FFFFh here and lives in 67FFh, so only the profile
                    // numbers are compared. A peer that contradicts itself is not read further.
                    if (logicalDevice == 0 && type.ProfileNumber != first.ProfileNumber)
                    {
                        end = CanOpenLogicalDeviceReadEnd.Failed;
                        failure = new InvalidDataException(
                            $"Object 1000h holds 0x{first.Value:X8}, profile {first.ProfileNumber}, but 67FFh, the device " +
                            $"type of logical device 0, holds 0x{type.Value:X8}, profile {type.ProfileNumber}: 1000h names " +
                            "the profile of the first logical device (CiA 301 v4.2.0 §7.5.2.1), so the peer's answers " +
                            "contradict each other.");
                        break;
                    }
                }
                catch (SdoAbortException abort)
                    when (abort.Origin == SdoAbortOrigin.Peer && abort.AbortCode == (uint)SdoAbortCode.ObjectDoesNotExist)
                {
                    // FFFFh says the node is a module of several logical devices (§7.5.2.1). The peer
                    // ending the chain before the second contradicts it; from the second on, it is
                    // how the chain ends. A description that refuses the object (below) is not the
                    // peer saying anything, and stays apart.
                    if (logicalDevice < 2)
                    {
                        end = CanOpenLogicalDeviceReadEnd.Failed;
                        failure = new InvalidDataException(
                            $"Object 1000h holds 0x{first.Value:X8}, whose additional information FFFFh announces several " +
                            $"logical devices (CiA 301 v4.2.0 §7.5.2.1), but the peer reports {abort.Index:X4}h, the " +
                            $"device type of logical device {logicalDevice}, as non-existent: its chain holds " +
                            $"{(logicalDevice == 0 ? "no logical device" : "one logical device")}.",
                            abort);
                    }

                    break;
                }
                catch (PeerSdoAccessException refused)
                {
                    // The client refused before sending anything, so the peer did not end the chain.
                    // With no file bound the caller can supply one; with one bound, it is the
                    // description that ends the chain.
                    end = refused.PeerDescriptionLoaded
                        ? CanOpenLogicalDeviceReadEnd.EndedByPeerDescription
                        : CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired;
                    break;
                }
                catch (Exception ex) when (ex is SdoAbortException or InvalidDataException)
                {
                    end = CanOpenLogicalDeviceReadEnd.Failed;
                    failure = ex;
                    break;
                }
            }
        }

        // When 67FFh was not read, the first device is described by 1000h: the list is never empty.
        if (devices.Count == 0) devices.Add(new CanOpenLogicalDevice(0, first));
        return new CanOpenLogicalDeviceReadResult(first, devices, end, failure);
    }

    private static async Task<CanOpenDeviceType> ReadDeviceTypeAsync(
        Func<ushort, CancellationToken, Task<byte[]>> uploadSubindexZero, ushort index,
        CancellationToken cancellationToken)
    {
        var data = await uploadSubindexZero(index, cancellationToken).ConfigureAwait(false);
        // UNSIGNED32. Taking four bytes of a longer value would report a type the peer never gave.
        if (!CanOpenDeviceType.TryParse(data, out var type))
        {
            throw new InvalidDataException(
                $"Object {index:X4}h:00 is UNSIGNED32 and four bytes long, but the peer returned {data.Length}.");
        }

        return type;
    }

    internal static void Validate(int logicalDevice, string paramName = "logicalDevice")
    {
        if (logicalDevice < 0 || logicalDevice >= MaxCount)
        {
            throw new ArgumentOutOfRangeException(paramName, logicalDevice,
                "A node has logical devices 0 to 7.");
        }
    }
}
