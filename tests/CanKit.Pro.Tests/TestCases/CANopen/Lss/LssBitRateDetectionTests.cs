using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// FR-CO-050: CiA 801 automatic bit-rate detection (<see cref="LssBitRateDetection"/>). Each
/// candidate is listened to in listen-only mode; a received frame confirms the rate, errors or a
/// degraded bus state rule it out, a silent window is inconclusive, and the search walks the
/// table over the passes allowed. The outcome of one listen is an adapter detail, so the error
/// frames, the frame and the bus state are driven through <see cref="DetectionProbeBus"/>; one
/// test runs the real listen-only path on the loopback adapter with live traffic.
/// </summary>
public class LssBitRateDetectionTests : IClassFixture<VirtualAdapterFixture>
{
    private static string NewSession() => VirtualAdapterFixture.NewSession("lss-autobaud");

    /// <summary>
    /// Runs <see cref="LssBitRateDetection.DetectCoreAsync"/> with a window that fires each
    /// scripted step in probe order on the bus just opened, so no test waits on a clock.
    /// </summary>
    private static Task<LssBitRateDetectionResult> DetectAsync(Func<int, ICanBus> factory,
        LssBitRateDetectionOptions options, IReadOnlyList<Action<DetectionProbeBus>> script,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var step = 0;
        Func<TimeSpan, CancellationToken, Task> delay = (_, _) =>
        {
            if (step < script.Count) script[step](DetectionProbeBus.Last!);
            step++;
            return Task.CompletedTask;
        };
        return LssBitRateDetection.DetectCoreAsync(factory, options, delay, cancellationToken);
    }

    private static Action<DetectionProbeBus> Frame(bool echo = false) => bus => bus.RaiseFrame(echo);
    private static Action<DetectionProbeBus> ErrorFrame() => bus => bus.RaiseFrame(isError: true);
    private static Action<DetectionProbeBus> Errors(int count) => bus => bus.RaiseErrors(count);
    private static Action<DetectionProbeBus> State(BusState state) => bus => bus.BusState = state;
    private static readonly Action<DetectionProbeBus> Silent = _ => { };

    // ---- the search --------------------------------------------------------------------------

    [Fact]
    public async Task A_Received_Frame_Detects_The_Candidate_And_Stops_The_Search()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000, 250_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Frame() });

        result.Detected.Should().BeTrue();
        result.BitsPerSecond.Should().Be(500_000);
        result.TableIndex.Should().Be(2, "500 kbit/s is CiA 301 table index 2");
        result.Probes.Should().ContainSingle("the first candidate answered, so the second is never tried");
        result.Probes[0].Outcome.Should().Be(LssBitRateProbeOutcome.Detected);
        opened.Should().ContainSingle().Which.IsDisposed.Should().BeTrue("every probe bus is disposed");
    }

    [Fact]
    public async Task An_Echo_Is_Not_Reception_But_A_Real_Frame_After_It_Is()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new Action<DetectionProbeBus>[] { bus => { bus.RaiseFrame(isEcho: true); bus.RaiseFrame(); } });

        result.BitsPerSecond.Should().Be(500_000, "the host's own echo is not the bus's traffic, but the frame after it is");
    }

    [Fact]
    public async Task An_Error_Frame_On_The_Rx_Stream_Is_Not_A_Reception()
    {
        // Some adapters surface error frames through FrameObserved too, flagged IsErrorFrame. At a
        // wrong rate such a frame must not be read as a confirmed reception.
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { ErrorFrame() });

        result.Detected.Should().BeFalse("an error frame is a wrong-rate symptom, not the bus's traffic");
        result.Probes[0].Outcome.Should().Be(LssBitRateProbeOutcome.Inconclusive);
    }

    [Fact]
    public async Task Error_Frames_At_The_Threshold_Reject_A_Candidate_And_The_Search_Moves_On()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 1_000_000, 500_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Errors(2), Frame() });

        result.BitsPerSecond.Should().Be(500_000);
        result.Probes.Should().HaveCount(2);
        result.Probes[0].Outcome.Should().Be(LssBitRateProbeOutcome.Rejected);
        result.Probes[0].ErrorFrames.Should().Be(2);
        result.Probes[0].BitsPerSecond.Should().Be(1_000_000);
        opened.Should().OnlyContain(bus => bus.IsDisposed);
    }

    [Fact]
    public async Task One_Error_Below_The_Threshold_Does_Not_Reject_The_Correct_Rate()
    {
        // CiA 801 §5.2.3 / §7: a single corrupted frame at the right rate must not move the
        // search on. With the default threshold of two, one error leaves the window to decide.
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Errors(1) });

        result.Detected.Should().BeFalse();
        result.Probes[0].Outcome.Should().Be(LssBitRateProbeOutcome.Inconclusive);
        result.Probes[0].ErrorFrames.Should().Be(1);
    }

    [Fact]
    public async Task A_Degraded_Bus_State_At_The_End_Of_The_Window_Rejects_The_Candidate()
    {
        // Some adapters move BusState without raising an error frame; a controller no longer
        // error-active after the window ruled the rate out.
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 1_000_000, 500_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { State(BusState.BusOff), Frame() });

        result.BitsPerSecond.Should().Be(500_000);
        result.Probes[0].Outcome.Should().Be(LssBitRateProbeOutcome.Rejected);
        result.Probes[0].BusState.Should().Be(BusState.BusOff);
    }

    [Fact]
    public async Task A_Silent_Bus_Within_The_Window_Is_Inconclusive_And_No_Rate_Is_Returned()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000, 250_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Silent, Silent });

        result.Detected.Should().BeFalse();
        result.BitsPerSecond.Should().BeNull();
        result.TableIndex.Should().BeNull();
        result.Probes.Should().OnlyContain(p => p.Outcome == LssBitRateProbeOutcome.Inconclusive);
        result.Probes.Select(p => p.BitsPerSecond).Should().Equal(500_000, 250_000);
    }

    [Fact]
    public async Task A_Second_Pass_Finds_A_Rate_That_Was_Silent_On_The_First()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000, 250_000 }, Passes = 2 };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Silent, Silent, Frame() });

        result.BitsPerSecond.Should().Be(500_000);
        result.Probes.Should().HaveCount(3, "both candidates on the first pass, then the first again on the second");
        result.Probes.Select(p => p.BitsPerSecond).Should().Equal(500_000, 250_000, 500_000);
    }

    [Fact]
    public async Task A_Fault_Ends_The_Detection_With_That_Exception_And_Disposes_The_Bus()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } };
        var boom = new InvalidOperationException("adapter gone");

        var detect = () => DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new Action<DetectionProbeBus>[] { bus => bus.RaiseFault(boom) });

        (await detect.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        opened.Should().ContainSingle().Which.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_Interrupts_The_Open_Window_And_Disposes_The_Probe_In_Progress()
    {
        var opened = new List<DetectionProbeBus>();
        // One candidate on purpose: with only the single probe, an OperationCanceledException can
        // come from nowhere but the window path. A second candidate would let the next-candidate
        // guard throw instead, which is not what this test is for.
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } };
        options.Validate();
        using var cancellation = new CancellationTokenSource();

        // The window honours its token, so cancelling it interrupts the probe in progress: the
        // delay is the production wait, linked to the caller's token, and the probe surfaces the
        // cancellation from the window itself rather than returning an inconclusive result.
        Func<TimeSpan, CancellationToken, Task> delay = (_, token) =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var detect = () => LssBitRateDetection.DetectCoreAsync(
            DetectionProbeBus.Factory(NewSession(), opened), options, delay, cancellation.Token);

        await detect.Should().ThrowAsync<OperationCanceledException>();
        opened.Should().ContainSingle().Which.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_Detected_Rate_Outside_The_Table_Has_No_Table_Index()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions { Candidates = new[] { 666_000 } };
        var result = await DetectAsync(DetectionProbeBus.Factory(NewSession(), opened), options,
            new[] { Frame() });

        result.BitsPerSecond.Should().Be(666_000);
        result.TableIndex.Should().BeNull("666 kbit/s is not a CiA 301 table rate");
    }

    // ---- listen-only and the opened rate -----------------------------------------------------

    [Fact]
    public async Task A_Bus_Opened_Outside_Listen_Only_Fails_When_It_Is_Required()
    {
        var opened = new List<DetectionProbeBus>();
        var factory = DetectionProbeBus.Factory(NewSession(), opened, ChannelWorkMode.Normal);

        var detect = () => LssBitRateDetection.DetectAsync(factory,
            new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } });

        await detect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*listen-only*");
        opened.Should().ContainSingle().Which.IsDisposed.Should().BeTrue("the bus is disposed even when the probe rejects it");
    }

    [Fact]
    public async Task Listen_Only_Can_Be_Waived_For_An_Adapter_That_Is_Silent_In_Normal_Mode()
    {
        var opened = new List<DetectionProbeBus>();
        var options = new LssBitRateDetectionOptions
        {
            Candidates = new[] { 500_000 },
            RequireListenOnly = false,
        };
        var factory = DetectionProbeBus.Factory(NewSession(), opened, ChannelWorkMode.Normal);
        var result = await DetectAsync(factory, options, new[] { Frame() });

        result.BitsPerSecond.Should().Be(500_000);
    }

    [Fact]
    public async Task A_Probe_Opened_At_The_Wrong_Rate_Fails_So_The_Result_Cannot_Name_It_Wrong()
    {
        var opened = new List<DetectionProbeBus>();
        // The factory always reports 500 kbit/s, whatever candidate it is asked for.
        var factory = DetectionProbeBus.Factory(NewSession(), opened, reportRate: 500_000);

        var detect = () => LssBitRateDetection.DetectAsync(factory,
            new LssBitRateDetectionOptions { Candidates = new[] { 250_000 } });

        await detect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nominal bit rate*");
    }

    [Fact]
    public async Task A_Factory_That_Returns_No_Bus_Fails()
    {
        var detect = () => LssBitRateDetection.DetectAsync(_ => null!,
            new LssBitRateDetectionOptions { Candidates = new[] { 500_000 } });

        await detect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no bus*");
    }

    // ---- a real listen-only probe on the loopback adapter ------------------------------------

    [Fact]
    public async Task Detects_The_Bus_On_The_Loopback_Adapter_With_Live_Heartbeat_Traffic()
    {
        var session = NewSession();
        using var producerBus = VirtualAdapterFixture.Open(session, 0);
        using var producer = CanOpen.OpenNode(producerBus, nodeId: 0x20);
        producer.StartHeartbeatProducer(TimeSpan.FromMilliseconds(20));

        var channel = 10;
        Func<int, ICanBus> openListenOnly = rate => CanBus.Open(
            $"virtual://{session}/{channel++}",
            cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(rate)
                .SetWorkMode(ChannelWorkMode.ListenOnly).EnableErrorInfo());

        var result = await LssBitRateDetection.DetectAsync(openListenOnly,
            new LssBitRateDetectionOptions { Candidates = new[] { 250_000, 500_000 } });

        // The loopback hub ignores the configured rate, so the first candidate with traffic wins;
        // the point is that the real listen-only open and the real FrameObserved path detect it.
        result.Detected.Should().BeTrue();
        result.BitsPerSecond.Should().Be(250_000);
        result.TableIndex.Should().Be(3);
    }

    // ---- options and result validation -------------------------------------------------------

    [Fact]
    public void The_Default_Candidates_Are_The_Cia301_Rates_With_The_Common_Three_First()
    {
        LssBitRateDetectionOptions.DefaultCandidates.Take(3).Should().Equal(500_000, 250_000, 125_000);
        LssBitRateDetectionOptions.DefaultCandidates.Should().BeEquivalentTo(
            LssBitTiming.Cia301Rates.Select(r => r.BitsPerSecond),
            "every CiA 301 table rate is a candidate");
        LssBitRateDetectionOptions.DefaultListenWindow.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_Default_Candidates_Cannot_Be_Mutated_Through_A_Downcast()
    {
        // They are the default of every options instance's Candidates, so a caller must not be
        // able to cast them back to the backing array and reorder or corrupt the shared list.
        (LssBitRateDetectionOptions.DefaultCandidates as int[]).Should().BeNull();
        ((System.Collections.Generic.ICollection<int>)LssBitRateDetectionOptions.DefaultCandidates)
            .IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Options_Reject_An_Empty_Duplicate_Or_Non_Positive_Candidate_List()
    {
        new Action(() => new LssBitRateDetectionOptions { Candidates = Array.Empty<int>() }.Validate())
            .Should().Throw<ArgumentException>();
        new Action(() => new LssBitRateDetectionOptions { Candidates = new[] { 500_000, 500_000 } }.Validate())
            .Should().Throw<ArgumentException>();
        new Action(() => new LssBitRateDetectionOptions { Candidates = new[] { 0 } }.Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Reject_A_Bad_Window_Threshold_Or_Pass_Count()
    {
        new Action(() => new LssBitRateDetectionOptions { ListenWindow = TimeSpan.Zero }.Validate())
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("ListenWindow");
        new Action(() => new LssBitRateDetectionOptions { ErrorFramesToReject = 0 }.Validate())
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("ErrorFramesToReject");
        new Action(() => new LssBitRateDetectionOptions { Passes = 0 }.Validate())
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Passes");
    }

    [Fact]
    public async Task DetectAsync_Rejects_A_Null_Factory()
    {
        await new Func<Task>(() => LssBitRateDetection.DetectAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void A_Result_Rejects_A_Null_Probe_List_And_A_Non_Positive_Rate()
    {
        new Action(() => new LssBitRateDetectionResult(500_000, null!)).Should().Throw<ArgumentNullException>();
        new Action(() => new LssBitRateDetectionResult(0, Array.Empty<LssBitRateProbe>()))
            .Should().Throw<ArgumentOutOfRangeException>();
        new LssBitRateDetectionResult(null, Array.Empty<LssBitRateProbe>()).Detected.Should().BeFalse();
    }
}
