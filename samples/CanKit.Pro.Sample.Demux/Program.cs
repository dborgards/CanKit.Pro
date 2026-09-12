using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Addressing;
using CanKit.Pro.RawCan;

// Runs entirely on CanKit's loopback ("virtual") adapter, so it needs no CAN hardware:
//
//   dotnet run --project samples/CanKit.Pro.Sample.Demux
//
// It shows the two things CanKit.Pro.RawCan adds on top of a plain ICanBus:
//   1. several independent, filtered views of one bus (instead of one shared ReceiveAsync), and
//   2. a uniform "was this frame actually sent" answer.

const string session = "cankit-pro-sample";

using var writer = CanBus.Open($"virtual://{session}/0",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

using var reader = CanBus.Open($"virtual://{session}/1",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

using var service = new CanBusService(reader);

// Two protocol instances, two disjoint ID ranges, two independent streams — neither competes with
// the other for frames, and neither blocks the other if it reads slowly.
using var diagnostics = service.Subscribe(CanIdFilter.Range(0x700, 0x7FF, CanFilterIDType.Standard));
using var telemetry = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var printers = Task.WhenAll(
    Print("diagnostics", diagnostics, stop.Token),
    Print("telemetry  ", telemetry, stop.Token));

writer.Transmit(CanFrame.Classic(0x701, new byte[] { 0x02, 0x10, 0x01 }));
writer.Transmit(CanFrame.Classic(0x123, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
writer.Transmit(CanFrame.Classic(0x7DF, new byte[] { 0x02, 0x3E, 0x00 }));

// A J1939 identifier composed from its fields rather than hand-rolled bit math.
var engineTemperature = J1939Id.ComposePgn(priority: 3, pgn: 0xFEEE, sourceAddress: 0x17);
Console.WriteLine($"J1939 PGN 0xFEEE from SA 0x17 -> CAN ID 0x{engineTemperature:X8}");

// SendConfirmed reports how the confirmation was obtained: a real TX echo where the bus provides
// one, otherwise driver acceptance — flagged as approximated so callers can tell the difference.
var confirmation = await service.SendConfirmed(CanFrame.Classic(0x201, new byte[] { 1, 2, 3 }));
Console.WriteLine($"SendConfirmed -> confirmed={confirmation.Confirmed} " +
                  $"approximated={confirmation.IsApproximated} reason={confirmation.FailureReason}");

// Nothing matches the filters any more; let the printers hit their own timeout and finish.
await printers;

Console.WriteLine("done");

static async Task Print(string label, ISubscription subscription, CancellationToken token)
{
    try
    {
        await foreach (var frameEvent in subscription.Frames.WithCancellation(token))
        {
            var frame = frameEvent.Frame;
            // Echoes are excluded by default, so `IsEcho` is false for everything printed here;
            // it is shown to make the shape of the delivered item visible in the sample.
            Console.WriteLine(
                $"[{label}] 0x{frame.ID:X3} len={frame.Len} echo={frameEvent.IsEcho} " +
                $"t={frameEvent.ReceiveTimestamp.TotalMilliseconds:F1}ms");
        }
    }
    catch (OperationCanceledException)
    {
        // The sample's own five-second budget expired: that is how it ends, not a failure.
    }
}
