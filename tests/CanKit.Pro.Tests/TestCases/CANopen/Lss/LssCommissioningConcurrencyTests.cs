using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Lss;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssCommissioningConcurrencyTests
{
    private static readonly LssAddress Address = new(1, 1, 1, 1);

    [Theory]
    [InlineData("configure", "configure")]
    [InlineData("configure", "inquire")]
    [InlineData("configure", "scan")]
    [InlineData("inquire", "configure")]
    [InlineData("inquire", "inquire")]
    [InlineData("inquire", "scan")]
    [InlineData("scan", "configure")]
    [InlineData("scan", "inquire")]
    [InlineData("scan", "scan")]
    public async Task Wrappers_Sharing_A_Master_Hold_The_Sequence_Through_Cleanup(string first, string second)
    {
        var paused = new PausedMaster();
        ILssMaster master = paused;
        var running = Run(new LssCommissioning(master), first);
        await paused.SelectionStarted.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        int callsBefore = paused.Calls.Count;
        var queued = Run(new LssCommissioning(master), second);
        try
        {
            paused.Calls.Count.Should().Be(callsBefore, "the queued sequence must not change the selection");
            queued.IsCompleted.Should().BeFalse();
            paused.AllowSelection.TrySetResult(true);
            await paused.CleanupStarted.Task.WaitAsync(LssTestTiming.SchedulingBudget);
            paused.Calls.Count.Should().Be(callsBefore + (first == "inquire" ? 3 : 2),
                "only the first sequence's services and cleanup may run");
            queued.IsCompleted.Should().BeFalse("the reservation includes the waiting-state cleanup");
        }
        finally
        {
            paused.AllowSelection.TrySetResult(true);
            paused.AllowCleanup.TrySetResult(true);
            await Task.WhenAll(running, queued).WaitAsync(LssTestTiming.SchedulingBudget);
        }

        paused.Selections.Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_A_Queued_Sequence_Sends_Nothing_And_Does_Not_Release_The_Owner()
    {
        var paused = new PausedMaster();
        ILssMaster master = paused;
        var tool = new LssCommissioning(master);
        var running = Run(tool, "configure");
        await paused.SelectionStarted.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        int callsBefore = paused.Calls.Count;
        using var cancel = new CancellationTokenSource();
        var cancelled = Run(new LssCommissioning(master), "scan", cancel.Token);
        cancel.Cancel();
        try
        {
            var act = async () => await cancelled.WaitAsync(LssTestTiming.SchedulingBudget);
            await act.Should().ThrowAsync<OperationCanceledException>();
            paused.Calls.Count.Should().Be(callsBefore);
            running.IsCompleted.Should().BeFalse();
        }
        finally
        {
            paused.AllowSelection.TrySetResult(true);
            paused.AllowCleanup.TrySetResult(true);
            await running.WaitAsync(LssTestTiming.SchedulingBudget);
        }

        await Run(tool, "inquire").WaitAsync(LssTestTiming.SchedulingBudget);
        paused.Selections.Should().Be(2);
    }

    [Fact]
    public async Task A_Failed_Sequence_Releases_The_Reservation_After_Cleanup()
    {
        var paused = new PausedMaster();
        ILssMaster master = paused;
        paused.FailSelection = true;
        var running = Run(new LssCommissioning(master), "configure");
        await paused.SelectionStarted.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        var queued = Run(new LssCommissioning(master), "inquire");
        try
        {
            paused.AllowSelection.TrySetResult(true);
            await paused.CleanupStarted.Task.WaitAsync(LssTestTiming.SchedulingBudget);
            queued.IsCompleted.Should().BeFalse();
        }
        finally
        {
            paused.AllowSelection.TrySetResult(true);
            paused.AllowCleanup.TrySetResult(true);
        }

        var act = async () => await running;
        await act.Should().ThrowAsync<InvalidOperationException>();
        await queued.WaitAsync(LssTestTiming.SchedulingBudget);
        paused.Selections.Should().Be(2);
    }

    private static Task Run(LssCommissioning tool, string operation, CancellationToken cancellationToken = default)
        => operation switch
        {
            "configure" => tool.ConfigureSlaveAsync(Address, new LssSlaveConfiguration { NodeId = 0x10 }, cancellationToken),
            "inquire" => tool.InquireSlaveAsync(Address, cancellationToken),
            "scan" => tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { AssignFromNodeId = 0x11, Limit = 1 }, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    // Pauses at selection and cleanup without relying on a scheduler delay or a real bus.
    private sealed class PausedMaster : ILssMaster
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource<bool> SelectionStarted { get; } = Signal();
        public TaskCompletionSource<bool> AllowSelection { get; } = Signal();
        public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
        public TaskCompletionSource<bool> AllowCleanup { get; } = Signal();
        public bool FailSelection { get; set; }
        public int Selections;
        private int _globals;

        public LssMasterOptions Options { get; } = new();

        public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(nameof(SwitchStateGlobalAsync));
            if (Interlocked.Increment(ref _globals) != 2) return Task.CompletedTask;
            CleanupStarted.TrySetResult(true);
            return AllowCleanup.Task;
        }

        public async Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(nameof(SwitchStateSelectiveAsync));
            await PauseSelectionAsync();
            return true;
        }

        public async Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(nameof(FastscanAsync));
            await PauseSelectionAsync();
            return LssFastscanResult.Found(Address);
        }

        public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
            => Record(nameof(ConfigureNodeIdAsync), LssServiceResult.Success);

        public Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
            => Record(nameof(InquireAddressAsync), Address);

        public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default)
            => Record(nameof(InquireNodeIdAsync), (byte)0x10);

        private Task<T> Record<T>(string name, T result)
        {
            Calls.Enqueue(name);
            return Task.FromResult(result);
        }

        public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
            bool returnToWaiting = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() { }

        public ValueTask DisposeAsync() => default;

        private async Task PauseSelectionAsync()
        {
            if (Interlocked.Increment(ref Selections) != 1) return;
            SelectionStarted.TrySetResult(true);
            await AllowSelection.Task;
            if (FailSelection) throw new InvalidOperationException("Selection failed.");
        }

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
