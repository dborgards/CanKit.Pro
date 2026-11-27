using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenApplicationSeamTests
{
    private sealed class AttachmentResource(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [Fact]
    public async Task A_Copied_Factory_Creates_Independent_Attachments_For_Each_Open()
    {
        var instances = new List<RecordingApplication>();
        var closed = new List<RecordingApplication>();
        var options = new CanOpenNodeOptions().WithApplicationFactory(() =>
        {
            var app = Entries();
            app.AttachmentResource = () => new AttachmentResource(() => closed.Add(app));
            instances.Add(app);
            return app;
        }).With(pdoCount: 8);
        instances.Should().BeEmpty();

        using var busA = Open(NewSession(), 1);
        using var busB = Open(NewSession(), 1);
        using var first = CanOpen.OpenNode(busA, Slave, options);
        using var second = CanOpen.OpenNode(busB, Slave, options);
        first.ObjectDictionary.WriteUnsigned(0x6000, 1, 42);
        second.ObjectDictionary.ReadUnsigned(0x6000, 1).Should().Be(5);
        Settle((CanOpenNode)first);
        Settle((CanOpenNode)second);
        await first.DisposeAsync();
        first.Dispose();
        closed.Should().Equal(instances[0]);
        second.Dispose();
        closed.Should().Equal(instances[0], instances[1]);
        instances[0].Should().NotBeSameAs(instances[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_Open_Releases_Successful_Attachments_In_Reverse_Order(bool failAtSubscribe)
    {
        var order = new List<string>();
        RecordingApplication Tracked(string name, bool throws) => new(name)
        {
            AttachmentResource = () => new AttachmentResource(() =>
            {
                order.Add(name);
                if (throws) throw new InvalidOperationException("release");
            }),
        };
        var first = Tracked("first", false);
        var last = Tracked("last", true);
        var failure = new InvalidOperationException("attach");
        var broken = new RecordingApplication("broken") { OnAttach = _ => throw failure };
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        if (failAtSubscribe) service.Dispose();
        var options = failAtSubscribe ? With(first, last) : With(first, last, broken);

        var thrown = Record.Exception(() => new CanOpenNode(service, Slave, options, ownsService: false));

        if (failAtSubscribe) thrown.Should().BeOfType<ObjectDisposedException>();
        else thrown.Should().BeSameAs(failure);
        order.Should().Equal("last", "first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_Startup_Releases_The_Subscription_And_Attachment(bool throwOnUnsubscribe)
    {
        int released = 0;
        var app = new RecordingApplication("startup failure")
        {
            // Inject failure in the startup post, after the bus subscription succeeded.
            OnAttach = host => NodeOf(host).ActorForTests.Dispose(),
            AttachmentResource = () => new AttachmentResource(() => released++),
        };
        int unsubscribed = 0;
        using var service = new StarvedReaderBusService
        {
            OnSubscriptionDisposed = () =>
            {
                unsubscribed++;
                if (throwOnUnsubscribe) throw new InvalidOperationException("unsubscribe");
            },
        };

        Assert.Throws<ObjectDisposedException>(() => CanOpen.OpenNode(service, Slave, With(app)));

        released.Should().Be(1);
        unsubscribed.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_Inside_A_Profile_Callback_Releases_Its_Resource_After_That_Callback()
    {
        var order = new List<string>();
        var app = Entries();
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.AttachmentResource = () => new AttachmentResource(() =>
        {
            order.Add("release");
            released.SetResult(true);
        });
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, With(app));
        Settle(node);
        app.Host!.Post(() =>
        {
            node.Dispose();
            order.Add("return");
        });
        await released.Task.WithTimeoutAsync(ShortTimeout);
        order.Should().Equal("return", "release");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OutOfMemoryException))]
    public async Task A_Resource_Released_After_Reentrant_Dispose_Reports_Its_Failure(Type exceptionType)
    {
        var failure = (Exception)Activator.CreateInstance(exceptionType)!;
        var app = Entries();
        app.AttachmentResource = () => new AttachmentResource(() => throw failure);
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, With(app));
        Settle(node);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        app.Host!.Post(node.Dispose);

        (await reported.Task.WithTimeoutAsync(ShortTimeout)).Should().BeSameAs(failure);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    public async Task A_Fatal_Cleanup_Failure_Propagates_And_Releases_Disposal_Waiters(Type exceptionType)
    {
        // Exercise the exception policy using a constructed exception, with a healthy runtime.
        var failure = (Exception)Activator.CreateInstance(exceptionType)!;
        var app = Entries();
        app.AttachmentResource = () => new AttachmentResource(() => throw failure);
        using var bus = Open(NewSession(), 1);
        var node = OpenNode(bus, With(app));
        Settle(node);

        Record.Exception(node.Dispose).Should().BeSameAs(failure);
        await node.DisposeAsync().AsTask().WithTimeoutAsync(ShortTimeout);
    }

    [Fact]
    public async Task An_Application_Post_Queued_Behind_Dispose_Cannot_Use_The_Attachment()
    {
        var app = Entries();
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, With(app));
        bool ran = false;
        Task? drained = null;
        await WhileActorHeldAsync(node, () =>
        {
            var closing = node.ActorForTests.PostAsync(node.Dispose);
            app.Host!.Post(() => ran = true);
            drained = Task.WhenAll(closing, node.ActorForTests.PostAsync(() => { }));
        });
        await drained!.WithTimeoutAsync(ShortTimeout);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task A_Dictionary_Writer_Finishes_Validation_Before_The_Attachment_Is_Released()
    {
        using var validating = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int releases = 0;
        var app = Entries();
        app.AttachmentResource = () => new AttachmentResource(() =>
        {
            Interlocked.Increment(ref releases);
            released.SetResult(true);
        });
        using var bus = Open(NewSession(), 1);
        using var node = OpenNode(bus, With(app));
        Settle(node);
        app.Validate = (_, _, _) =>
        {
            validating.Set();
            resume.Wait(ShortTimeout).Should().BeTrue();
            return OdWriteDecision.Accept;
        };
        var write = Task.Run(() => node.ObjectDictionary.WriteUnsigned(0x6000, 1, 42));
        try
        {
            validating.Wait(ShortTimeout).Should().BeTrue();
            node.Dispose();
            Volatile.Read(ref releases).Should().Be(0);
        }
        finally { resume.Set(); }
        await write;
        await released.Task.WithTimeoutAsync(ShortTimeout);
        app.Validate = (_, _, _) => throw new InvalidOperationException("detached validator");
        node.ObjectDictionary.WriteUnsigned(0x6000, 1, 43);
        releases.Should().Be(1);
    }
}
