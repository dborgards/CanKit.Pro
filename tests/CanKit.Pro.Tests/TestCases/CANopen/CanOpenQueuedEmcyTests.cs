using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.TestCases.CANopen.Lss;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public class CanOpenQueuedEmcyTests
{
    private static LssTestService Service() => new()
    {
        Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true }),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Emcy_Reaching_The_Actor_After_Disposal_Is_Dropped(bool asynchronousFailure)
    {
        using var service = Service();
        using var node = new CanOpenNode(service, 5, new CanOpenNodeOptions(), ownsService: false);
        await node.DisposeAsync();
        int calls = 0;
        Task Transmit()
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }
        Func<Task> dispatch = asynchronousFailure
            ? () => node.DispatchQueuedEmcy(Transmit, _ => Task.FromException<Task>(new ObjectDisposedException("ProtocolActor")))
            : () => node.DispatchQueuedEmcy(Transmit);
        await dispatch.Should().NotThrowAsync();
        Volatile.Read(ref calls).Should().Be(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_Unexpected_Actor_Dispatch_Failure_Is_Still_Reported(bool asynchronousFailure, bool disposedException)
    {
        using var service = Service();
        using var node = new CanOpenNode(service, 5, new CanOpenNodeOptions(), ownsService: false);
        Exception error = disposedException
            ? new ObjectDisposedException("unexpected actor failure")
            : new InvalidOperationException("unexpected actor failure");
        Task<Task> Post(Func<Task> work)
        {
            if (asynchronousFailure) return Task.FromException<Task>(error);
            throw error;
        }
        Func<Task> dispatch = () => node.DispatchQueuedEmcy(() => Task.CompletedTask, Post);
        (await dispatch.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Application_Emcy_Failure_Is_Preserved_Even_If_It_Disposes_The_Node(bool dispose)
    {
        using var service = Service();
        using var node = new CanOpenNode(service, 5, new CanOpenNodeOptions(), ownsService: false);
        var error = new ObjectDisposedException("application callback");
        Task Transmit()
        {
            if (dispose) node.Dispose();
            throw error;
        }
        Func<Task> dispatch = () => node.DispatchQueuedEmcy(Transmit);
        (await dispatch.Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(error);
    }
}
