using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The flying-master rig: a node on a virtual clock, a peer bus, a frame log and an actor witness,
/// shared by every test class that drives the NMT master (<c>using static</c>). The clock is
/// virtual. A step moves it, then waits until the actor has fired what became due and until a
/// control frame those timers handed to <c>Task.Run</c> has come back.
/// </summary>
internal static class FlyingMasterRig
{
    internal static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(2000);

    internal const byte LeftId = 0x10;
    internal const byte WitnessForLeft = 0x7E;

    internal const ushort Startup = 0x1F80;
    internal const ushort Timing = 0x1F90;
    internal const uint MasterBits = 0x21;
    internal const uint SuppressSelfStart = 0x04;
    internal const uint SuppressSlaveStart = 0x08;
    internal const uint Assigned = 0x01;
    internal const uint BootSlave = 0x04;
    internal const uint MandatorySlave = 0x08;

    internal static string NewSession() => $"canopen-fm-{Guid.NewGuid():N}";

    internal static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    internal static CanOpenNode OpenClockedNode(ICanBus bus, byte nodeId, ManualTimeSource clock,
        CanOpenDeviceDescription? description = null, CanOpenNodeOptions? options = null)
        => new(new CanBusService(bus), nodeId, options ?? new CanOpenNodeOptions(), ownsService: true,
            timeSource: clock, description: description);

    /// <summary>Short times, still ordered so a better priority level always waits less than a
    /// worse one: the device slot is narrowed before the priority slot, or the write is rejected.</summary>
    internal static void Tighten(CanOpenNode node)
    {
        var od = node.ObjectDictionary;
        od.WriteUnsigned(Timing, 0x01, 20);
        od.WriteUnsigned(Timing, 0x02, 40);
        od.WriteUnsigned(Timing, 0x05, 1);
        od.WriteUnsigned(Timing, 0x04, 200);
        od.WriteUnsigned(Timing, 0x06, 0);
    }

    internal static bool IsNmt((uint Id, byte[] Data) frame, NmtCommand command, byte target)
        => frame.Id == CanOpenCobId.NmtCommand && frame.Data.Length >= 2
           && frame.Data[0] == (byte)command && frame.Data[1] == target;

    internal static void TransmitHeartbeat(ICanBus peer, byte nodeId, byte state)
        => peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(nodeId)),
            new[] { state }, isExtendedFrame: false));

    internal static void TransmitNmt(ICanBus peer, NmtCommand command, byte target)
        => peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new[] { (byte)command, target }, isExtendedFrame: false));

    internal static MasterRig OpenMaster()
    {
        var session = NewSession();
        var nodeBus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        var node = OpenClockedNode(nodeBus, LeftId, clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft),
            new FrameLog(nodeBus, peer), peer, nodeBus);
    }

    internal sealed class MasterRig : IDisposable
    {
        private readonly ICanBus _nodeBus;

        public MasterRig(ManualTimeSource clock, CanOpenNode node, ActorWitness witness, FrameLog log,
            ICanBus peer, ICanBus nodeBus)
        {
            Clock = clock;
            Node = node;
            Witness = witness;
            Log = log;
            Peer = peer;
            _nodeBus = nodeBus;
            Witness.SdoProgress = () => Log.SdoFramesObserved;
        }

        public ManualTimeSource Clock { get; }
        public CanOpenNode Node { get; }
        public ActorWitness Witness { get; }
        public FrameLog Log { get; }
        public ICanBus Peer { get; }

        /// <summary>A caller-owned peer sharing the rig's clock and actor/send settling.</summary>
        public CanOpenNode OpenPeerNode(byte nodeId, CanOpenDeviceDescription? description = null,
            CanOpenNodeOptions? options = null)
        {
            var peer = OpenClockedNode(Peer, nodeId, Clock, description, options);
            // Separate witness IDs prevent one peer's round trip spending another's signal.
            Witness.Peers.Add(new ActorWitness(peer, _nodeBus, (byte)(0x7C - Witness.Peers.Count)));
            return peer;
        }

        public void Dispose()
        {
            Node.Dispose();
            Log.Dispose();
            Peer.Dispose();
            _nodeBus.Dispose();
        }
    }

    internal static async Task UntilAsync(ManualTimeSource clock, ActorWitness left, ActorWitness? right,
        Func<bool> done, int virtualMs, string why)
    {
        for (int elapsed = 0; elapsed < virtualMs && !done(); elapsed += 10)
            await AdvanceAsync(clock, left, right, TimeSpan.FromMilliseconds(10));
        done().Should().BeTrue(why);
    }

    internal static async Task AdvanceAsync(ManualTimeSource clock, ActorWitness left, ActorWitness? right, TimeSpan by)
    {
        clock.Advance(by);
        await QuiesceAsync(left, right);
    }

    /// <summary>Settle every actor and its driver handoffs before moving protocol time.
    /// Between safety-verification reads, let the worker post its next transfer first.</summary>
    internal static async Task QuiesceAsync(ActorWitness left, ActorWitness? right)
    {
        var roots = right is null ? new[] { left } : new[] { left, right };
        var witnesses = roots.Concat(roots.SelectMany(w => w.Peers)).ToArray();
        var idle = Stopwatch.StartNew();
        long progress = roots.Sum(w => w.SdoProgress());
        while (true)
        {
            var before = new List<object[]>();
            foreach (var witness in roots)
                before.Add((await witness.CaptureVerificationWorkAsync()).ActiveTransfers);
            foreach (var witness in witnesses) await witness.SettleAsync();
            foreach (var witness in witnesses) await witness.SendsHandedOverAsync();
            foreach (var witness in witnesses) await witness.SettleAsync();
            foreach (var afterRound in roots.Select(w => w.AfterRound).OfType<Func<Task>>())
                await afterRound();
            bool pending = false;
            bool changed = false;
            for (int i = 0; i < roots.Length; i++)
            {
                var work = await roots[i].CaptureVerificationWorkAsync();
                pending |= work.Waiting;
                if (work.Waiting) roots[i].ContinuationObserved?.Invoke();
                changed |= !SameTransfers(before[i], work.ActiveTransfers);
            }
            if (!pending && !changed) return;

            long now = roots.Sum(w => w.SdoProgress());
            if (now != progress || changed) { progress = now; idle.Restart(); }
            if (idle.Elapsed > ShortTimeout)
                throw new TimeoutException("A safety verification continuation made no SDO progress.");
            await Task.Delay(1); // cooperative host scheduling; protocol time remains frozen
        }
    }

    private static bool SameTransfers(object[] before, object[] after)
    {
        if (before.Length != after.Length) return false;
        for (int i = 0; i < before.Length; i++)
            if (!ReferenceEquals(before[i], after[i])) return false;
        return true;
    }

    /// <summary>Queues data frames observed on one bus.</summary>
    internal sealed class FrameLog : IDisposable
    {
        private readonly ICanBus[] _buses;
        private readonly object _gate = new();
        private readonly List<(uint Id, byte[] Data)> _frames = new();
        private long _sdoFrames;

        public long SdoFramesObserved => Volatile.Read(ref _sdoFrames);

        public FrameLog(params ICanBus[] buses)
        {
            _buses = buses;
            foreach (var bus in _buses) bus.FrameObserved += OnFrame;
        }

        public List<(uint Id, byte[] Data)> Snapshot()
        {
            lock (_gate) return _frames.ToList();
        }

        public void Clear()
        {
            lock (_gate) _frames.Clear();
        }

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame) return;
            if (frame.ID is >= 0x580 and <= 0x67F) Interlocked.Increment(ref _sdoFrames);
            lock (_gate) _frames.Add(((uint)frame.ID, frame.Data.ToArray()));
        }

        public void Dispose()
        {
            foreach (var bus in _buses) bus.FrameObserved -= OnFrame;
        }
    }

    /// <summary>Same two-round-trip witness as the life-guarding tests: a heartbeat from a peer
    /// nobody consumes, delivered twice, proves the actor has fired the timers the clock made due.</summary>
    internal sealed class ActorWitness
    {
        private readonly ICanBus _peerBus;
        private readonly byte _witnessPeer;
        private readonly CanOpenNode _node;
        private readonly SemaphoreSlim _seen = new(0, int.MaxValue);
        private readonly HashSet<byte> _heldVerificationPeers = new();
        private int _markersSent;

        public List<ActorWitness> Peers { get; } = new();
        public Func<long> SdoProgress { get; set; } = () => 0;
        public Action? ContinuationObserved { get; set; }
        public Func<Task>? AfterRound { get; set; }
        public uint MarkerCobId => CanOpenCobId.Heartbeat(_witnessPeer);
        public int MarkersSent => Volatile.Read(ref _markersSent);

        public IDisposable HoldPeerSdoChannel(byte peer, SemaphoreSlim channel)
        {
            channel.Wait();
            var permission = AllowPeerVerificationWait(peer);
            return new ReleaseOnce(() =>
            {
                permission.Dispose();
                channel.Release();
            });
        }

        public IDisposable AllowPeerVerificationWait(byte peer)
        {
            lock (_heldVerificationPeers) _heldVerificationPeers.Add(peer);
            return new ReleaseOnce(() =>
            {
                lock (_heldVerificationPeers) _heldVerificationPeers.Remove(peer);
            });
        }

        public async Task<(bool Waiting, object[] ActiveTransfers)> CaptureVerificationWorkAsync()
        {
            (byte[] PendingPeers, object[] ActiveTransfers) work = (Array.Empty<byte>(), Array.Empty<object>());
            await _node.PostToActorAsync(() => work = _node.CaptureBootVerificationWorkForTests());
            bool waiting;
            lock (_heldVerificationPeers) waiting = work.PendingPeers.Any(peer => !_heldVerificationPeers.Contains(peer));
            return (waiting, work.ActiveTransfers);
        }

        private sealed class ReleaseOnce : IDisposable
        {
            private Action? _release;
            public ReleaseOnce(Action release) => _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        public ActorWitness(CanOpenNode node, ICanBus peerBus, byte witnessPeer)
        {
            _node = node;
            _peerBus = peerBus;
            _witnessPeer = witnessPeer;
            node.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId == witnessPeer) _seen.Release();
            };
        }

        public async Task SettleAsync()
        {
            await PokeAsync();
            await PokeAsync();
        }

        /// <summary>Until no send of the node has passed its transmit gate without reaching the bus.
        /// A send a test double holds has reached it. The budget bounds a thread-pool hand-off.</summary>
        public async Task SendsHandedOverAsync()
        {
            var deadline = DateTime.UtcNow + ShortTimeout;
            while (_node.HasTransmitsAwaitingDriverForTests)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"A send of the node did not reach the bus within {ShortTimeout}.");
                await Task.Delay(1);
            }
        }

        private async Task PokeAsync()
        {
            _peerBus.Transmit(CanFrame.Classic(unchecked((int)MarkerCobId),
                new[] { (byte)NmtState.PreOperational }, isExtendedFrame: false));
            Interlocked.Increment(ref _markersSent);
            if (!await _seen.WaitAsync(ShortTimeout).ConfigureAwait(false))
                throw new TimeoutException($"The node did not report the witness heartbeat within {ShortTimeout}.");
        }
    }
}
