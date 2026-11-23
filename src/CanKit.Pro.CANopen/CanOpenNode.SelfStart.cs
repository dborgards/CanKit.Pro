using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private IDeadline? _selfStartDeadline;
    // Created when 075h is issued. Earlier 074h frames belong to someone else's request.
    private CancellationTokenSource? _selfStartAttempt;
    private Task _selfStartSendOutcome = Task.CompletedTask;
    internal Task SelfStartSendOutcomeForTests => Volatile.Read(ref _selfStartSendOutcome);

    private bool CanSelfStart => _disposed == 0 && _state == NmtState.PreOperational;

    private void BeginSelfStart()
    {
        if (!_options.EnableSelfStartingNodes || !CanSelfStart) return;
        switch (ReadStartup())
        {
            case 8: // Table 16, local autonomous start
                StartLocalNode();
                break;
            case NmtStartAllNodesBit: // Table 16, startup-capable slave
                var delay = _od.ReadUnsigned(Co.SelfStartTiming, 2);
                _selfStartDeadline = _deadlines.Arm(TimeSpan.FromMilliseconds(delay), RequestMasterDetection);
                break;
        }
    }

    private void RequestMasterDetection()
    {
        if (!CanSelfStart || ReadStartup() != NmtStartAllNodesBit) return;
        _selfStartAttempt = new CancellationTokenSource();
        var token = _selfStartAttempt.Token;
        var request = SendControlFrame(CanOpenCobId.NmtMasterDetectionRequest, Array.Empty<byte>(), token);
        AfterSelfStartSend(request, token, WaitForMaster);
    }

    private void WaitForMaster()
    {
        // A master seen during the additional node slot also suppresses the optional Start.
        // A delayed driver confirmation cannot shorten either wait.
        var timeout = _od.ReadUnsigned(Co.SelfStartTiming, 1);
        var slot = _od.ReadUnsigned(Co.SelfStartTiming, 3);
        var wait = TimeSpan.FromMilliseconds(timeout + _nodeId * slot);
        _selfStartDeadline = _deadlines.Arm(wait, StartNetworkIfUnmanaged);
    }

    private void StartNetworkIfUnmanaged()
    {
        if (!CanSelfStart || ReadStartup() != NmtStartAllNodesBit) return;
        var token = _selfStartAttempt!.Token;
        var start = EnqueueNmt(NmtCommand.Start, 0, token);
        AfterSelfStartSend(start, token, StartLocalNode);
    }

    // On a bus without self-reception the confirmed broadcast also starts us locally.
    private void StartLocalNode() => ApplyNmtTransition(NmtState.Operational);

    private void AfterSelfStartSend(Task<bool> send, CancellationToken token, Action onConfirmed)
    {
        Volatile.Write(ref _selfStartSendOutcome, send.ContinueWith(completed => RunOnActor(() =>
        {
            if (token.IsCancellationRequested || _disposed != 0) return;
            if (completed.Status == TaskStatus.RanToCompletion && completed.Result) onConfirmed();
            else CancelSelfStart();
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
    }

    private void CancelSelfStart()
    {
        _selfStartDeadline?.Dispose();
        _selfStartDeadline = null;
        _selfStartAttempt?.Cancel();
        _selfStartAttempt?.Dispose();
        _selfStartAttempt = null;
    }
}
