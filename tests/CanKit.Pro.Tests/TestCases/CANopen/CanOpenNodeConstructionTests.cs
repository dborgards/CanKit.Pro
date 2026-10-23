using System;
using System.IO;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// A node whose construction fails leaves nothing running (#357 C-10). Observed through
/// <see cref="ProtocolActor.RunningLoopCount"/>, the one thing that can see an actor nobody holds
/// a reference to; the collection keeps every other test from starting or ending an actor between
/// the two readings.
/// </summary>
[Collection(ActorLoopCountCollection.Name)]
public class CanOpenNodeConstructionTests
{
    // The dictionary is filled after the actor's thread has started, and a description that
    // cannot be applied threw out of the constructor with that thread still running: there was
    // no node to dispose, and the dedicated thread lived until the process ended.
    [Fact]
    public void A_Description_That_Cannot_Be_Applied_Leaves_No_Actor_Running()
    {
        var sep = Path.DirectorySeparatorChar;
        var description = CanOpenDeviceDescription.Load(
            $"{AppContext.BaseDirectory.TrimEnd('/', '\\')}{sep}TestCases{sep}CANopen{sep}Fixtures{sep}device.eds");
        // An object the loader cannot read: a sub-object slot with nothing in it.
        var broken = new CanOpenObject { Index = 0x2345 };
        broken.SubObjects[0x00] = null!;
        description.Eds!.ObjectDictionary.Objects[0x2345] = broken;
        using var bus = ControllableBus.EchoCapable($"canopen-ctor-failure-{Guid.NewGuid():N}");
        using var service = new CanBusService(bus);
        var loopsBefore = ProtocolActor.RunningLoopCount;

        Action open = () => CanOpen.OpenNode(service, 0x05, description);

        open.Should().Throw<NullReferenceException>("the object cannot be applied");
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore,
            "the actor the node created must not outlive the construction that failed");
        service.SubscriptionCount.Should().Be(0, "the node never got as far as subscribing");
    }
}
