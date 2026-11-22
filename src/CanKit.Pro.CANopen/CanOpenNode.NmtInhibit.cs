using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly object _nmtTimingLock = new();
    private TimeSpan _nmtInterval;
    private long? _lastNmtCompletion;
    private TaskCompletionSource<bool>? _nmtIntervalWait;

    // Actor-owned commands expire with their boot or master role. A reaction issued after
    // HaltBootWork receives a new token and can still send its requested Stop or Reset.
    private CancellationTokenSource? _timedMasterCommands;

    private void CancelTimedMasterCommands()
    {
        _timedMasterCommands?.Cancel();
        _timedMasterCommands?.Dispose();
        _timedMasterCommands = null;
    }

    private void ApplyNmtInhibit()
    {
        if (!_options.EnableNmtInhibit) return;
        var interval = TimeSpan.FromTicks(_od.ReadUnsigned(Co.NmtInhibit, 0) * 1000L);
        lock (_nmtTimingLock)
        {
            _nmtInterval = interval;
            _nmtIntervalWait?.TrySetResult(true);
        }
    }

    private Task<bool> QueueTimedNmt(byte[] payload, CancellationToken token, bool masterOwned)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = RunTimedNmt(_nmtOrder, payload, masterOwned, token, result);
        _nmtOrder = send.ContinueWith(FinishNmt, result, CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        return result.Task;
    }

    private async Task<bool> RunTimedNmt(Task previous, byte[] payload, bool masterOwned, CancellationToken caller,
        TaskCompletionSource<bool> result)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, _readerCts.Token);
        var token = lifetime.Token;
        // A cancelled caller can leave immediately. Its queue position stays behind its
        // predecessor so that a third command can never overtake an in-flight send.
        using (token.Register(() => result.TrySetCanceled(token)))
        {
            await previous.ConfigureAwait(false);
            await WaitForNmtInterval(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        try
        {
            var send = masterOwned ? SendNmtReporting(payload, token)
                : SendControlFrame(CanOpenCobId.NmtCommand, payload, token);
            return await send.ConfigureAwait(false);
        }
        finally
        {
            lock (_nmtTimingLock) _lastNmtCompletion = _actor.TimeSource.GetTimestamp();
        }
    }

    private async Task WaitForNmtInterval(CancellationToken token)
    {
        var clock = _actor.TimeSource;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long due;
            TaskCompletionSource<bool> wake;
            lock (_nmtTimingLock)
            {
                if (!_lastNmtCompletion.HasValue) return;
                due = TickMath.DueAt(_lastNmtCompletion.Value, _nmtInterval, clock.Frequency);
                if (clock.GetTimestamp() >= due) return;
                _nmtIntervalWait = wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                using var timer = _actor.ScheduleAt(due, () => wake.TrySetResult(true));
                using var registration = token.Register(() => wake.TrySetCanceled(token));
                await wake.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_nmtTimingLock) _nmtIntervalWait = null;
            }
        }
    }
}
