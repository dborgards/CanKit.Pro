using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

// CANopen quickstart: configure a device and stream its data
// ===========================================================
//
// CANopen (CiA 301) is how many machines, drives and sensors talk over CAN. Every device — a
// *node* — has a number from 1 to 127 and an *object dictionary* (OD): a table of everything it
// can tell you or be told, addressed as index:subindex, e.g. 0x1000:00 = "device type".
// Two ways to use it:
//   SDO  "service data object" — ask a node for one entry, or set one. Slow, confirmed, any size.
//        Think: configuration.
//   PDO  "process data object" — a node pushes a few mapped OD entries in ONE frame, again and
//        again, no questions asked. Think: measurements.
// NMT (network management) switches nodes between states; PDOs only flow in "Operational".
//
// This sample needs no device and no hardware. It
//   1. opens two virtual CAN buses wired together and puts a CANopen node on each — a
//      "master" (node 1, us) and a "slave" (node 17, the device),
//   2. reads a standard entry from the slave over SDO,
//   3. shows why the master needs a device description before it touches a custom entry,
//   4. writes and reads back that entry over SDO,
//   5. starts both nodes and lets the slave stream the entry to the master as a PDO.
//
//   dotnet run --project samples/CanKit.Pro.Sample.CanOpenQuickstart
//   dotnet run --project samples/CanKit.Pro.Sample.CanOpenQuickstart -- --pro   (adds the extras)
//
// Words you will meet:
//   OD       object dictionary. TPDO = transmit PDO (the slave sends), RPDO = receive PDO.
//   EDS      electronic data sheet: the text file a device maker ships that describes every
//            OD entry of a device (name, type, access). Step 3 explains why we need one.
//   COB-ID   the CAN ID of a CANopen message; the library derives the defaults from the node ID.

var pro = args.Contains("--pro");

// ── 1. Two virtual buses, two nodes ────────────────────────────────────────────────────────
Step("1. Open two virtual CAN buses and a CANopen node on each (master = 1, slave = 17)");
var session = $"canopen-sample-{Guid.NewGuid():N}";
using var busMaster = OpenBus(session, channel: 0);
using var busSlave = OpenBus(session, channel: 1);
using var master = CanOpen.OpenNode(busMaster, nodeId: 0x01);
using var slave = CanOpen.OpenNode(busSlave, nodeId: 0x11);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // never wait forever

// ── 2. Read a standard entry ───────────────────────────────────────────────────────────────
// 0x1000:00 (device type) exists on every CANopen node, and the client may read it without
// knowing anything else about the device. SdoUploadAsync = "read from the server"
// (CANopen names transfers from the server's point of view). The answer is raw little-endian bytes.
Step("2. Read the slave's device type (SDO, entry 0x1000:00)");
var deviceType = await master.SdoUploadAsync(serverNodeId: 0x11, index: 0x1000, subindex: 0x00, cts.Token);
Console.WriteLine($"   raw bytes {BitConverter.ToString(deviceType)}, value 0x{BitConverter.ToUInt32(deviceType, 0):X8} " +
                  "(0 = no device profile)");

// ── 3. A custom entry needs a description ──────────────────────────────────────────────────
// Real devices have entries the standard does not define. We give the slave one: a 16-bit
// "process value" at 0x2000:00. Reading it over SDO would need the master to know its size and
// type, and by default the library refuses to guess: it only allows a handful of standard
// entries until it has been told what the peer looks like.
Step("3. Try to read a custom entry (0x2000:00) without a device description");
slave.ObjectDictionary.AddU16(0x2000, 0x00, 0x0000);
try
{
    await master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2000, subindex: 0x00, cts.Token);
}
catch (PeerSdoAccessException ex)
{
    Console.WriteLine($"   refused, as intended: {ex.Message.Split('.')[0]}.");
}

// Hand the master the slave's EDS. In a real project you would load the file the device maker
// ships: CanOpenDeviceDescription.Load("mydevice.eds"). Here the text is embedded (see PeerFiles
// at the bottom) so the sample is self-contained.
Step("4. Give the master the slave's device description (EDS), then write and read 0x2000:00");
var description = CanOpenDeviceDescription.ParseEds(PeerFiles.SlaveEds);
master.BindPeerDeviceDescription(0x11, description);

// SdoDownloadAsync = "write to the server". 0x1234 as two little-endian bytes.
await master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2000, subindex: 0x00,
    new byte[] { 0x34, 0x12 }, cts.Token);
var readBack = await master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2000, subindex: 0x00, cts.Token);
Console.WriteLine($"   wrote 0x1234, read back {BitConverter.ToString(readBack)} " +
                  $"(the slave's own OD says 0x{slave.ObjectDictionary.ReadUnsigned(0x2000, 0x00):X4})");

// ── 5. Stream the entry as a PDO ───────────────────────────────────────────────────────────
// The slave maps 0x2000:00 into its TPDO 1 and sends it every 100 ms (an event timer). The
// master maps the incoming frame into a local OD entry (0x2100:00) via RPDO 1. Mapping =
// "which OD entries, how many bits, in which order". The TPDO's COB-ID is the default one for
// node 17, and the RPDO listens on exactly that COB-ID.
Step("5. Map the entry into a PDO: the slave transmits it every 100 ms, the master receives it");
slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, bitLength: 16),
    transmission: TpdoTransmission.EventTimer, eventTimerInterval: TimeSpan.FromMilliseconds(100));

master.ObjectDictionary.AddU16(0x2100, 0x00, 0x0000);
master.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, bitLength: 16),
    cobId: CanOpenCobId.TpdoDefault(nodeId: 0x11, pdoIndex: 1));

var received = new SemaphoreSlim(0);
EventHandler<RpdoReceivedEventArgs> onRpdo = (_, e) =>
{
    Console.WriteLine($"   PDO on COB-ID 0x{e.CobId:X3}: {BitConverter.ToString(e.Payload)} -> " +
                      $"master OD 0x2100 = 0x{master.ObjectDictionary.ReadUnsigned(0x2100, 0x00):X4}");
    received.Release();
};
master.RpdoReceived += onRpdo;

// PDOs only flow while a node is Operational, and an NMT "start" command moves a node there
// (both booted into Pre-operational). A node never receives its own frames, so it cannot start
// itself: the master starts the slave, and — because both nodes live in this one process — the
// slave starts the master. On a real network some NMT master starts everybody.
await master.SendNmtCommandAsync(NmtCommand.Start, targetNodeId: 0x11, cts.Token);
await slave.SendNmtCommandAsync(NmtCommand.Start, targetNodeId: 0x01, cts.Token);
await received.WaitAsync(cts.Token);

// The slave application changes the value; the next PDO carries the new one.
slave.ObjectDictionary.WriteUnsigned(0x2000, 0x00, 0x0042);
await received.WaitAsync(cts.Token);
await received.WaitAsync(cts.Token);
master.RpdoReceived -= onRpdo;                       // the slave keeps sending; we stop printing

Console.WriteLine();
Console.WriteLine("Done.");

if (!pro)
{
    Console.WriteLine("Run with '-- --pro' for heartbeats, emergencies and SDO aborts.");
    return;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Pro extras (dotnet run ... -- --pro)
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ── A. Heartbeat: is the device still alive? ───────────────────────────────────────────────
// A node can announce "I'm here, and this is my NMT state" every N ms. A consumer that expects
// those heartbeats raises an event when one is overdue. Here the slave stops its heartbeat and
// the master notices after its 300 ms limit.
Step("A. Heartbeat producer/consumer: stop the heartbeat and watch the master notice");
{
    var timedOut = new TaskCompletionSource<HeartbeatTimeoutEventArgs>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var beats = 0;
    master.HeartbeatReceived += (_, e) =>
    {
        if (e.ProducerNodeId == 0x11) Interlocked.Increment(ref beats);
    };
    master.HeartbeatTimeout += (_, e) => timedOut.TrySetResult(e);

    slave.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100));
    master.AddHeartbeatConsumer(producerNodeId: 0x11, timeout: TimeSpan.FromMilliseconds(300));
    await Task.Delay(450, cts.Token);
    Console.WriteLine($"   {beats} heartbeats from node 17 in ~450 ms (slave state: {slave.State})");

    slave.StopHeartbeatProducer();
    var late = await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
    Console.WriteLine($"   heartbeat timeout: node {late.ProducerNodeId} silent for more than {late.Timeout.TotalMilliseconds:F0} ms");
    master.RemoveHeartbeatConsumer(0x11);
}

// ── B. Emergency messages ──────────────────────────────────────────────────────────────────
// When something goes wrong, a node broadcasts an EMCY: a 16-bit error code (CiA 301 defines the
// families — 0x2xxx is "current", 0x3xxx "voltage" ...), the error register (bit 0 = generic) and
// up to five manufacturer-specific bytes.
Step("B. Emergency: the slave reports an overcurrent");
{
    var seen = new TaskCompletionSource<EmcyMessageInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    master.EmcyReceived += (_, e) => seen.TrySetResult(
        new EmcyMessageInfo(e.Message.ProducerNodeId, e.Message.ErrorCode, e.Message.ErrorRegister));
    await slave.SendEmcyAsync(errorCode: 0x2310, errorRegister: 0x03, cancellationToken: cts.Token);
    var info = await seen.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
    Console.WriteLine($"   EMCY from node {info.Node}: error code 0x{info.Code:X4}, error register 0x{info.Register:X2}");
}

// ── C. The server can say no ───────────────────────────────────────────────────────────────
// The EDS lists 0x2001, but this slave's firmware does not implement it (as happens when a data
// sheet is newer than the device). The master lets the request through — the description says the
// entry exists — and the slave answers with an SDO abort. The abort code says why.
Step("C. SDO abort: read an entry the description lists but the device does not have");
try
{
    await master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00, cts.Token);
}
catch (SdoAbortException ex)
{
    Console.WriteLine($"   SdoAbortException at 0x{ex.Index:X4}:{ex.Subindex:X2}, abort code 0x{ex.AbortCode:X8} " +
                      $"({(SdoAbortCode)ex.AbortCode}), " +
                      (ex.Origin == SdoAbortOrigin.Peer ? "sent by the other node" : "raised locally"));
}

// ── D. SYNC-driven PDOs and more ───────────────────────────────────────────────────────────
// Not shown here, but in the same API: synchronous PDOs triggered by SYNC (StartSyncProducer),
// node guarding, block SDO transfers for large data, loading a whole node from an EDS/DCF, and
// the flying-master election of CiA 302. To find out who is on a real bus without sending
// anything, see samples/CanKit.Pro.Sample.CanOpenBusScan.

Console.WriteLine();
Console.WriteLine("Pro extras done. src/CanKit.Pro.CANopen/README.md lists everything the node can do.");

static ICanBus OpenBus(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

static void Step(string title) => Console.WriteLine($"\n{title}");

internal readonly record struct EmcyMessageInfo(byte Node, ushort Code, byte Register);

// The slave's device description in EDS format (an INI-style text file). A real one lists the
// mandatory communication objects and everything the device maker added; this one lists only what
// the sample touches: the process value 0x2000, and 0x2001, which the sample slave lacks.
internal static class PeerFiles
{
    public const string SlaveEds = """
        [FileInfo]
        FileName=slave.eds
        FileVersion=1
        FileRevision=0
        EDSVersion=4.0
        Description=CANopen quickstart slave
        CreationTime=10:00AM
        CreationDate=09-26-2026
        CreatedBy=CanKit.Pro
        [DeviceInfo]
        VendorName=CanKit.Pro
        VendorNumber=0
        ProductName=Quickstart slave
        ProductNumber=0
        RevisionNumber=0
        OrderCode=QS
        BaudRate_500=1
        SimpleBootUpSlave=1
        Granularity=8
        NrOfRXPDO=0
        NrOfTXPDO=0
        [ManufacturerObjects]
        SupportedObjects=2
        1=0x2000
        2=0x2001
        [2000]
        ParameterName=Process value
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=0
        PDOMapping=1
        [2001]
        ParameterName=Not implemented by the firmware
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=0
        PDOMapping=0
        """;
}
