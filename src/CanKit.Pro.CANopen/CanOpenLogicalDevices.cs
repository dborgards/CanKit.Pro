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
        // The static helper validates the index, so the range is defined in one place.
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
    /// <summary>The peer itself ended the chain: <c>1000h</c> does not carry <c>FFFFh</c>, the peer
    /// aborted a device-type object with "object does not exist", or eight devices were read.
    /// Every logical device the peer has is in the list.</summary>
    Complete = 0,

    /// <summary><c>1000h</c> says more logical devices follow, but the SDO client refused to read
    /// <c>67FFh</c> because no peer description is bound for the node. Bind one with
    /// <see cref="ICanOpenNode.BindPeerDeviceDescription"/> and read again.</summary>
    PeerDescriptionRequired = 1,

    /// <summary>A read inside the chain ended in an SDO abort other than the peer's "object does
    /// not exist", the client's own timeout included, or returned a value that is not four bytes.
    /// <see cref="CanOpenLogicalDeviceReadResult.Failure"/> says which, and the list holds the
    /// devices read before it.</summary>
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
    /// device is reported with the type of <c>1000h</c> when <c>67FFh</c> could not be read.</summary>
    public IReadOnlyList<CanOpenLogicalDevice> Devices { get; }

    /// <summary>How the reading ended.</summary>
    public CanOpenLogicalDeviceReadEnd End { get; }

    /// <summary>The failure that ended the reading. Not <see langword="null"/> exactly when
    /// <see cref="End"/> is <see cref="CanOpenLogicalDeviceReadEnd.Failed"/>.</summary>
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
    private const ushort FirstArea = 0x6000;
    private const int AreaSize = 0x800;
    private const int PdosPerLogicalDevice = 64;

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
    /// A peer without <c>FFFFh</c> is one logical device. With it, the chain ends at the first
    /// device-type object the peer reports as non-existent, or after eight devices.
    /// </para>
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
    /// the chain, an SDO abort other than the peer's "object does not exist", the client's own
    /// timeout included, and a value that is not four bytes end it as <see cref="CanOpenLogicalDeviceReadEnd.Failed"/>
    /// with the devices read so far. Anything else, a transport fault
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
                    devices.Add(new CanOpenLogicalDevice(logicalDevice, type));
                }
                catch (SdoAbortException abort)
                    when (abort.Origin == SdoAbortOrigin.Peer && abort.AbortCode == (uint)SdoAbortCode.ObjectDoesNotExist)
                {
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

        // A peer that announces further devices but has no 67FFh is still one, described by 1000h.
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

    private static void Validate(int logicalDevice)
    {
        if (logicalDevice < 0 || logicalDevice >= MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalDevice), logicalDevice,
                "A node has logical devices 0 to 7.");
        }
    }
}
