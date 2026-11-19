using System;
using System.Collections.Generic;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Reliability;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The attach of a CiA 408 device against a host whose posted work the test runs, in the order it
/// chooses: the window between the device publishing its host and its posted start, which a real
/// node never lets a test into.
/// </summary>
public class Cia408DeviceAttachTests
{
    [Fact]
    public void A_Fault_Posted_Before_The_Start_Runs_Raises_The_Start_First()
    {
        var host = new HoldingHost();
        var device = new Cia408Device(Cia408Rig.Valve);
        var changes = new List<string>();
        device.StateChanged += (_, e) => changes.Add($"{e.Previous}->{e.Current} [{string.Join(",", e.Transitions)}]");

        device.Attach(host);
        device.ReportFault(0x2110, Cia408FaultState.FaultInit);
        host.Posted.Should().HaveCount(2, "the start and the fault");
        host.Posted[1]();
        host.Posted[0]();

        changes.Should().Equal("NotReady->Init [0,1]", "Init->FaultInit [8,13]");
    }

    [Fact]
    public void A_Fault_Posted_Before_The_Start_Does_Not_Step_The_Model_Before_It_Was_Started()
    {
        var host = new HoldingHost { Now = TimeSpan.FromSeconds(5) };
        var model = new Steps();
        var device = new Cia408Device(Cia408Rig.Valve, model);

        device.Attach(host);
        device.ReportFault(0x2110, Cia408FaultState.FaultInit);
        host.Posted[1]();
        host.Posted[0]();

        model.Seen.Should().Equal(new[] { (Cia408State.FaultInit, TimeSpan.Zero) },
            "one step once the model has started, in the state the device rests in, with no time elapsed");
    }

    private sealed class Steps : ICia408Process
    {
        internal List<(Cia408State, TimeSpan)> Seen { get; } = new();

        public void Step(Cia408ProcessContext context) => Seen.Add((context.State, context.Elapsed));
    }

    private sealed class NoDeadline : IDeadline
    {
        public bool IsExpired => false;

        public bool IsCompleted => false;

        public bool IsCancelled => false;

        public bool Rearm(TimeSpan timeout) => true;

        public bool Complete() => true;

        public void Dispose()
        {
        }
    }

    /// <summary>A host that keeps what is posted for the test to run, and does nothing else.</summary>
    private sealed class HoldingHost : ICanOpenApplicationHost
    {
        internal List<Action> Posted { get; } = new();

        public ObjectDictionary ObjectDictionary { get; } = new();

        public NmtState State => NmtState.PreOperational;

        public TimeSpan Now { get; set; }

        public bool CommunicationFailureActive => false;

        public IDeadline Arm(TimeSpan delay, Action onExpired) => new NoDeadline();

        public void Post(Action action) => Posted.Add(action);

        public void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType)
        {
        }

        public void SetDefaultTpdo(int pdoNumber, PdoMapping mapping, bool enabled = true)
        {
        }

        public void SetDefaultRpdo(int pdoNumber, PdoMapping mapping)
        {
        }

        public void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries)
        {
        }

        public void RequestTransmissionOfTpdo(int pdoNumber)
        {
        }

        public void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default)
        {
        }

        public void ClearError(ushort errorCode)
        {
        }

        public void SendWarning(ushort errorCode, ReadOnlySpan<byte> manufacturerSpecific = default)
        {
        }

        public NmtState? ErrorBehaviorTarget(byte subindex) => null;

        public void RequestNmtState(NmtState target)
        {
        }
    }
}
