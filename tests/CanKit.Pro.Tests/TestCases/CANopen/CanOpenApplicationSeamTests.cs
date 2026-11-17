using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The application seam: a device profile handed to the node in
/// <see cref="CanOpenNodeOptions.Applications"/> is attached when the node opens, its claims and
/// requirements are checked then, and what it declares in <c>Attach</c> is part of the node's
/// power-on state.
/// </summary>
/// <remarks>
/// In the loop-count collection because one test counts the actors that are running: that count
/// is process-wide and only means something when no other test creates or ends one.
/// </remarks>
[Collection(ActorLoopCountCollection.Name)]
public partial class CanOpenApplicationSeamTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte Master = 0x01;
    private const byte Slave = 0x11;

    private static string NewSession() => VirtualAdapterFixture.NewSession("canopen-app-seam");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

    private static CanOpenNode OpenNode(ICanBus bus, CanOpenNodeOptions options, ITimeSource? clock = null,
        CanOpenDeviceDescription? description = null)
        => new(new CanBusService(bus), Slave, options, ownsService: true, clock, description);

    private static CanOpenNodeOptions With(params ICanOpenApplication[] applications)
        => applications.Aggregate(new CanOpenNodeOptions(), (options, application) => options.WithApplication(application));

    private static void Settle(CanOpenNode node)
    {
        node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
        node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
    }

    private static void Advance(ManualTimeSource clock, CanOpenNode node, TimeSpan by)
    {
        Settle(node);
        clock.Advance(by);
        Settle(node);
    }

    /// <summary>A description that declares the record <c>6000h</c>: sub-index 0 and 1, both U8. Its
    /// <c>1000h</c> holds <paramref name="deviceType"/>, and it declares each of
    /// <paramref name="deviceTypeObjects"/> as a read-only UNSIGNED32 with that value.</summary>
    private static CanOpenDeviceDescription DescriptionWith6000(uint deviceType = 0,
        params (ushort Index, uint Word)[] deviceTypeObjects) => CanOpenDeviceDescription.ParseEds(
        "[FileInfo]\n" +
        "FileName=seam.eds\n" +
        "FileVersion=1\n" +
        "FileRevision=0\n" +
        "EDSVersion=4.0\n" +
        "Description=application seam test device\n" +
        "CreationTime=10:00AM\n" +
        "CreationDate=09-26-2026\n" +
        "CreatedBy=CanKit.Pro tests\n" +
        "[DeviceInfo]\n" +
        "VendorName=CanKit.Pro\n" +
        "VendorNumber=0\n" +
        "ProductName=Seam\n" +
        "ProductNumber=0\n" +
        "RevisionNumber=0\n" +
        "OrderCode=SEAM\n" +
        "BaudRate_500=1\n" +
        "SimpleBootUpMaster=0\n" +
        "SimpleBootUpSlave=1\n" +
        "Granularity=8\n" +
        "DynamicChannelsSupported=0\n" +
        "GroupMessaging=0\n" +
        "NrOfRXPDO=0\n" +
        "NrOfTXPDO=0\n" +
        "LSS_Supported=0\n" +
        "[MandatoryObjects]\n" +
        "SupportedObjects=3\n" +
        "1=0x1000\n" +
        "2=0x1001\n" +
        "3=0x1018\n" +
        "[1000]\n" +
        "ParameterName=Device type\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0007\n" +
        "AccessType=ro\n" +
        $"DefaultValue=0x{deviceType:X8}\n" +
        "PDOMapping=0\n" +
        "[1001]\n" +
        "ParameterName=Error register\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0005\n" +
        "AccessType=ro\n" +
        "DefaultValue=0\n" +
        "PDOMapping=0\n" +
        "[1018]\n" +
        "ParameterName=Identity\n" +
        "SubNumber=2\n" +
        "ObjectType=0x9\n" +
        "[1018sub0]\n" +
        "ParameterName=Highest sub-index supported\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0005\n" +
        "AccessType=ro\n" +
        "DefaultValue=1\n" +
        "PDOMapping=0\n" +
        "[1018sub1]\n" +
        "ParameterName=Vendor-ID\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0007\n" +
        "AccessType=ro\n" +
        "DefaultValue=0\n" +
        "PDOMapping=0\n" +
        "[OptionalObjects]\n" +
        "SupportedObjects=0\n" +
        "[ManufacturerObjects]\n" +
        $"SupportedObjects={1 + deviceTypeObjects.Length}\n" +
        "1=0x6000\n" +
        string.Concat(deviceTypeObjects.Select((o, n) => $"{n + 2}=0x{o.Index:X4}\n")) +
        "[6000]\n" +
        "ParameterName=Record\n" +
        "ObjectType=0x9\n" +
        "SubNumber=2\n" +
        "[6000sub0]\n" +
        "ParameterName=Highest sub-index supported\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0005\n" +
        "AccessType=ro\n" +
        "DefaultValue=1\n" +
        "PDOMapping=0\n" +
        "[6000sub1]\n" +
        "ParameterName=Value\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0005\n" +
        "AccessType=rw\n" +
        "DefaultValue=0\n" +
        "PDOMapping=0\n" +
        string.Concat(deviceTypeObjects.Select(o =>
            $"[{o.Index:X4}]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\n" +
            $"DefaultValue=0x{o.Word:X8}\nPDOMapping=0\n")));

    /// <summary>Counts the boot-up messages (heartbeat state 0) of one producer.</summary>
    private sealed class BootupWatch
    {
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public BootupWatch(ICanOpenNode observer, byte producer)
        {
            observer.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId != producer || e.State != NmtState.Initializing) return;
                switch (Interlocked.Increment(ref _seen))
                {
                    case 1: _first.TrySetResult(true); break;
                    case 2: _second.TrySetResult(true); break;
                }
            };
        }

        public Task First => _first.Task.WithTimeoutAsync(ShortTimeout);

        public Task Second => _second.Task.WithTimeoutAsync(ShortTimeout);
    }

    /// <summary>An application that does what its delegates say, and writes down what the node
    /// does to it.</summary>
    private sealed class RecordingApplication : ICanOpenApplication
    {
        private readonly object _gate = new();
        private readonly List<string> _log = new();

        public RecordingApplication(string name, params CanOpenIndexRange[] claims)
        {
            Name = name;
            ClaimedRanges = claims;
        }

        public string Name { get; }

        public IReadOnlyList<CanOpenIndexRange> ClaimedRanges { get; }

        public CanOpenApplicationRequirements Requirements { get; set; } = CanOpenApplicationRequirements.None;

        public Action<ICanOpenApplicationHost>? OnAttach { get; set; }

        public Func<ushort, byte, byte[], OdWriteDecision>? Validate { get; set; }

        public Action<IReadOnlyList<CanOpenObjectAddress>>? WhenWritten { get; set; }

        public Action<NmtState, NmtState>? WhenNmtChanged { get; set; }

        public Action<NmtCommand>? WhenReset { get; set; }

        public Action<bool>? WhenFailureChanged { get; set; }

        /// <summary>What the node called, in order; a copy, so the actor can keep appending.</summary>
        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_gate) return _log.ToArray();
            }
        }

        public ICanOpenApplicationHost? Host { get; private set; }

        private void Record(string entry)
        {
            lock (_gate) _log.Add(entry);
        }

        /// <summary>Adds a line to the log on behalf of the test, so that what happens elsewhere (an
        /// event the node raises, say) lands in the same order as the node's calls.</summary>
        public void Note(string entry) => Record(entry);

        public void Attach(ICanOpenApplicationHost host)
        {
            Host = host;
            Record("attach");
            OnAttach?.Invoke(host);
        }

        public OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value)
            => Validate?.Invoke(index, subindex, value) ?? OdWriteDecision.Accept;

        public void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries)
        {
            Record("written " + string.Join(",", entries.Select(e => $"{e.Index:X4}:{e.Subindex:X2}")));
            WhenWritten?.Invoke(entries);
        }

        public void OnNmtStateChanged(NmtState previous, NmtState current)
        {
            Record($"nmt {previous}>{current}");
            WhenNmtChanged?.Invoke(previous, current);
        }

        public void OnReset(NmtCommand command)
        {
            Record($"reset {command}");
            WhenReset?.Invoke(command);
        }

        public void OnCommunicationFailureChanged(bool active)
        {
            Record($"failure {active}");
            WhenFailureChanged?.Invoke(active);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Attach at open.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_Application_Is_Attached_Before_The_Node_Boots()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        master.BindPeerDeviceDescription(Slave, DescriptionWith6000());
        var bootups = new BootupWatch(master, Slave);
        var app = new RecordingApplication("recorder");
        NmtState? stateInAttach = null;
        app.OnAttach = host =>
        {
            stateInAttach = host.State;
            host.ObjectDictionary.AddU8(0x6000, 0x01, 0x5A);
        };

        using var slave = OpenNode(busB, With(app));
        await bootups.First;

        var raw = await master.SdoUploadAsync(Slave, 0x6000, 0x01).WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(0x5A);
        app.Log.Should().StartWith("attach");
        stateInAttach.Should().Be(NmtState.Initializing, "the node has not booted yet");
    }

    [Fact]
    public void Applications_Are_Attached_In_List_Order()
    {
        using var bus = Open(NewSession(), 1);
        var order = new List<string>();
        var first = new RecordingApplication("first", new CanOpenIndexRange(0x6000, 0x67FF)) { OnAttach = _ => order.Add("first") };
        var second = new RecordingApplication("second", new CanOpenIndexRange(0x6800, 0x6FFF)) { OnAttach = _ => order.Add("second") };

        using var node = OpenNode(bus, With(first, second));

        order.Should().Equal("first", "second");
    }

    [Fact]
    public void A_Description_Is_Applied_Before_Attach()
    {
        using var bus = Open(NewSession(), 1);
        var app = new RecordingApplication("recorder");
        bool? declared = null;
        app.OnAttach = host => declared = host.ObjectDictionary.ContainsIndex(0x6000);

        using var node = OpenNode(bus, With(app), description: DescriptionWith6000());

        declared.Should().BeTrue("the description's objects exist when the application attaches");
    }

    // -----------------------------------------------------------------------------------------
    // Claims and requirements: all of them fail the open, with the parameter and the names.
    // -----------------------------------------------------------------------------------------

    private static ArgumentException OpenFails(CanOpenNodeOptions options)
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        var failure = Assert.Throws<ArgumentException>(
            () => new CanOpenNode(service, Slave, options, ownsService: false));
        failure.ParamName.Should().Be("options");
        return failure;
    }

    [Fact]
    public void A_Claim_Below_2000h_Fails_The_Open()
    {
        var app = new RecordingApplication("low", new CanOpenIndexRange(0x1F00, 0x2000));

        var failure = OpenFails(With(app));

        failure.Message.Should().Contain("low").And.Contain("0x1F00-0x2000");
        app.Log.Should().BeEmpty("a claim that is refused is refused before anything is attached");
    }

    [Fact]
    public void Overlapping_Claims_Fail_The_Open()
    {
        var alpha = new RecordingApplication("alpha", new CanOpenIndexRange(0x6000, 0x67FF));
        var beta = new RecordingApplication("beta", new CanOpenIndexRange(0x6700, 0x6FFF));

        var failure = OpenFails(With(alpha, beta));

        failure.Message.Should().Contain("alpha").And.Contain("beta");
        alpha.Log.Should().BeEmpty();
    }

    [Fact]
    public void Two_Overlapping_Claims_Of_One_Application_Fail_The_Open()
    {
        var app = new RecordingApplication("single", new CanOpenIndexRange(0x6000, 0x67FF), new CanOpenIndexRange(0x6000, 0x6000));

        OpenFails(With(app)).Message.Should().Contain("single");
    }

    [Fact]
    public void The_Same_Application_Twice_Fails_The_Open()
    {
        // No claims, so that listing it twice is the only thing wrong with the options.
        var app = new RecordingApplication("twice");

        OpenFails(With(app, app)).Message.Should().Contain("twice").And.Contain("twice in");
    }

    [Fact]
    public void A_Pdo_Requirement_Above_PdoCount_Fails_The_Open()
    {
        var app = new RecordingApplication("hungry") { Requirements = new CanOpenApplicationRequirements { PdoCount = 5 } };

        var failure = OpenFails(With(app).With(pdoCount: 4));

        failure.Message.Should().Contain("PdoCount").And.Contain("5");
    }

    [Fact]
    public void Failure_Monitoring_Without_ErrorBehavior_Fails_The_Open()
    {
        var app = new RecordingApplication("watcher")
        {
            Requirements = new CanOpenApplicationRequirements { CommunicationFailureMonitoring = true },
        };

        OpenFails(With(app)).Message.Should().Contain("EnableErrorBehavior");
    }

    [Fact] // Review Focus 5
    public void An_Attach_That_Throws_Fails_The_Open_And_Ends_The_Actor()
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        var app = new RecordingApplication("broken") { OnAttach = _ => throw new InvalidOperationException("no device") };
        var loopsBefore = ProtocolActor.RunningLoopCount;

        Action open = () => _ = new CanOpenNode(service, Slave, With(app), ownsService: false);

        open.Should().Throw<InvalidOperationException>().WithMessage("no device");
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore, "the actor of the node that failed to open must end");
        service.SubscriptionCount.Should().Be(0, "the node never got as far as subscribing");
    }

    // -----------------------------------------------------------------------------------------
    // The options.
    // -----------------------------------------------------------------------------------------

    [Fact] // Review Focus 4
    public void Options_Copies_Keep_Their_Applications()
    {
        var first = new RecordingApplication("first");
        var second = new RecordingApplication("second");
        var original = new CanOpenNodeOptions();

        var one = original.WithApplication(first);

        original.Applications.Should().BeEmpty("WithApplication returns a copy");
        one.Applications.Should().Equal(first);
        one.With(pdoCount: 8).Applications.Should().Equal(first);
        one.WithLss(null).Applications.Should().Equal(first);
        one.WithSrdoConfigurationCheckInterval(null).Applications.Should().Equal(first);
        one.WithErrorHistoryCapacity(3).Applications.Should().Equal(first);
        one.With(pdoCount: 8).WithLss(null).WithApplication(second).Applications.Should().Equal(first, second);
        FluentActions.Invoking(() => original.WithApplication(null!)).Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------------------
    // What the application declares is power-on state.
    // -----------------------------------------------------------------------------------------

    private static RecordingApplication DeclaringApplication() => new("device", CanOpenIndexRange.ForLogicalDevice(0))
    {
        OnAttach = host => host.ObjectDictionary.AddU8(0x6000, 0x01, 1),
    };

    private sealed class ResetRig : IDisposable
    {
        private readonly ICanBus _busA;
        private readonly ICanBus _busB;
        internal readonly ICanOpenNode MasterNode;
        internal readonly CanOpenNode SlaveNode;
        internal readonly BootupWatch Bootups;

        internal ResetRig(RecordingApplication app)
        {
            var session = NewSession();
            _busA = Open(session, 0);
            _busB = Open(session, 1);
            MasterNode = CanOpen.OpenNode(_busA, nodeId: Master);
            Bootups = new BootupWatch(MasterNode, Slave);
            SlaveNode = OpenNode(_busB, With(app));
        }

        internal ObjectDictionary Od => SlaveNode.ObjectDictionary;

        internal uint Value => Od.ReadUnsigned(0x6000, 0x01);

        internal async Task ResetAsync(NmtCommand command)
        {
            await Bootups.First;
            await MasterNode.SendNmtCommandAsync(command, Slave);
            await Bootups.Second;
        }

        public void Dispose()
        {
            SlaveNode.Dispose();
            MasterNode.Dispose();
            _busB.Dispose();
            _busA.Dispose();
        }
    }

    [Fact]
    public async Task Reset_Node_Restores_What_The_Application_Declared()
    {
        using var rig = new ResetRig(DeclaringApplication());
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Value.Should().Be(1u, "Reset Node returns the application's objects to their attach-time value");
    }

    [Fact]
    public async Task Reset_Communication_Leaves_Application_Objects_Alone()
    {
        using var rig = new ResetRig(DeclaringApplication());
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        rig.Value.Should().Be(9u, "Reset Communication restores 1000h-1FFFh only");
    }

    [Fact]
    public async Task A_Stored_Application_Value_Survives_Reset_Node()
    {
        using var rig = new ResetRig(DeclaringApplication());
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);
        rig.SlaveNode.StoreParameters();
        rig.Od.WriteUnsigned(0x6000, 0x01, 3);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Value.Should().Be(9u, "1010h saves the application's object with the rest");
    }

    [Fact]
    public async Task Restoring_Defaults_Returns_The_Application_Value_To_What_Attach_Declared()
    {
        using var rig = new ResetRig(DeclaringApplication());
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);
        rig.SlaveNode.StoreParameters();
        rig.SlaveNode.RestoreDefaultParameters();

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Value.Should().Be(1u, "1011h load returns the application's object to its factory value");
    }

    // -----------------------------------------------------------------------------------------
    // The host.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Deadlines_Run_On_The_Nodes_Clock()
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource();
        int fired = 0;
        TimeSpan nowAtAttach = default;
        var app = new RecordingApplication("timer")
        {
            OnAttach = host =>
            {
                nowAtAttach = host.Now;
                host.Arm(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref fired));
            },
        };
        using var node = OpenNode(bus, With(app), clock);

        Settle(node);
        Volatile.Read(ref fired).Should().Be(0, "no time has passed on the node's clock");

        Advance(clock, node, TimeSpan.FromMilliseconds(49));
        Volatile.Read(ref fired).Should().Be(0, "the deadline is 50 ms away");

        Advance(clock, node, TimeSpan.FromMilliseconds(1));
        Volatile.Read(ref fired).Should().Be(1);
        (app.Host!.Now - nowAtAttach).Should().Be(TimeSpan.FromMilliseconds(50), "Now reads the node's clock");
    }
}
