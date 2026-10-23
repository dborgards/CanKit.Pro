using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// The collection exists only so these tests run with nothing else running: RunningLoopCount is
/// process-wide, so a before/after comparison is only meaningful when no other test can create or
/// end an actor between the two readings.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActorLoopCountCollection
{
    public const string Name = "ProtocolActor.RunningLoopCount";
}

[Collection(ActorLoopCountCollection.Name)]
public class ProtocolActorLoopCountTests
{
    // A failed start leaves no loop whose finally could undo the count or dispose what the
    // constructor created, and the caller has no instance to Dispose (#209).
    [Theory]
    [InlineData(ActorExecutionMode.DedicatedThread)]
    [InlineData(ActorExecutionMode.ThreadPool)]
    [InlineData(ActorExecutionMode.SynchronizationContext)]
    public void A_Failed_Loop_Start_Releases_The_Loop_Count_And_Its_Disposables(ActorExecutionMode mode)
    {
        var loopsBefore = ProtocolActor.RunningLoopCount;
        ProtocolActor? seen = null;
        var context = mode == ActorExecutionMode.SynchronizationContext ? new SynchronizationContext() : null;

        Action construct = () => new ProtocolActor(mode, context, timeSource: null, shutdownTimeout: null,
            beforeLoopStart: actor =>
            {
                seen = actor;
                ProtocolActor.RunningLoopCount.Should().Be(loopsBefore + 1, "the count is taken before the start");
                throw new OutOfMemoryException("simulated start failure");
            });

        construct.Should().Throw<OutOfMemoryException>().WithMessage("simulated start failure");

        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore, "no loop exists to end the count it started");
        seen.Should().NotBeNull();
        var stopCts = (CancellationTokenSource)Field(seen!, "_stopCts");
        var signal = (SemaphoreSlim)Field(seen!, "_signal");
        var token = () => stopCts.Token;
        token.Should().Throw<ObjectDisposedException>("the constructor's cancellation source must be released");
        var release = () => signal.Release();
        release.Should().Throw<ObjectDisposedException>("the constructor's semaphore must be released");
    }

    // A-5 (2026-10-21 deep review): the loop catches its own cancellation and ends normally, so a
    // loop task that faults has failed outside every callback scope -- here in the clock, the one
    // thing FinalDrain calls that RunSafely does not guard. WaitForLoopTask caught that fault as
    // "the loop exited via OperationCanceledException", reported a clean join and dropped the
    // exception; and the throw out of FinalDrain skipped the rest of the loop's tail, so the loop
    // count stayed taken. In this collection because the count is process-wide.
    [Theory]
    [InlineData(ActorExecutionMode.ThreadPool)]
    [InlineData(ActorExecutionMode.SynchronizationContext)]
    public void A_Loop_That_Faults_Outside_Every_Callback_Is_Reported_Once_And_Releases_Its_Count(ActorExecutionMode mode)
    {
        var loopsBefore = ProtocolActor.RunningLoopCount;
        var context = mode == ActorExecutionMode.SynchronizationContext ? new SynchronizationContext() : null;
        var clock = new FailingTimeSource();
        using var actor = new ProtocolActor(mode, context, clock, shutdownTimeout: null);
        var reported = new List<Exception>();
        actor.BackgroundExceptionOccurred += (_, ex) => { lock (reported) reported.Add(ex); };

        // Nothing armed, so the live loop never reads the clock; the first read is FinalDrain's.
        var failure = new InvalidOperationException("the clock failed");
        clock.Failure = failure;
        actor.Dispose();
        actor.Dispose();

        lock (reported)
        {
            reported.Should().ContainSingle("the fault is reported, and only once although both callers join")
                .Which.Should().BeSameAs(failure);
        }
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore, "a loop that threw out of FinalDrain still ends its count");
    }

    // Codex on #389: a loop still inside a callback when Dispose gives up joining it, and faulting
    // only once that callback returns, was observed by nobody -- Wait had already returned false
    // and no caller was waiting any more. The join is shortened and the callback held on a gate,
    // so the join cannot succeed (deterministic: it times out by construction); the fault then has
    // to be reported on its own once the gate opens. The 10 s bound is a hang guard only.
    [Fact]
    public async Task A_Loop_That_Faults_After_Dispose_Gave_Up_On_It_Is_Still_Reported()
    {
        var clock = new FailingTimeSource();
        using var actor = new ProtocolActor(ActorExecutionMode.ThreadPool, null, clock, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? timeout = null;
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is TimeoutException) timeout = ex;
            else fault.TrySetResult(ex);
        };

        actor.Post(() =>
        {
            started.Set();
            release.Wait();
        });
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var failure = new InvalidOperationException("the clock failed");
        clock.Failure = failure;

        actor.Dispose();
        timeout.Should().BeOfType<TimeoutException>("the join gave up while the callback was held");

        release.Set();
        (await fault.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeSameAs(failure,
            "the loop faulted after Dispose had returned, and that fault is reported all the same");
    }

    private sealed class FailingTimeSource : ITimeSource
    {
        public Exception? Failure { get; set; }

        public long Frequency => TimeSpan.TicksPerSecond;

        public long GetTimestamp() => Failure is { } failure ? throw failure : 0;
    }

    private static object Field(ProtocolActor actor, string name) =>
        typeof(ProtocolActor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!;
}
