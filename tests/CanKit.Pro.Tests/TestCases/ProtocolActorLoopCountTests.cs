using System;
using System.Reflection;
using System.Threading;
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

    private static object Field(ProtocolActor actor, string name) =>
        typeof(ProtocolActor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!;
}
