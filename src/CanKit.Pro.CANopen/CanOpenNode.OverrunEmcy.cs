using System;
using System.Threading;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.CANopen;

// #423 / FR-CO-011: error-frame notifications report lost objects, not a repairable
// controller level. One latched cause ends only at application acknowledgement or NMT reset.
internal sealed partial class CanOpenNode
{
    private ICanBus? _controllerOverrunBus;
    private OverrunObservation? _controllerOverrunObservation;
    private bool _controllerOverrunActive;

    private sealed class OverrunObservation
    {
        internal int Pending;
    }

    private void InitializeControllerOverrunMonitor()
    {
        if (!_options.EmitControllerOverrunEmcy) return;
        _controllerOverrunBus = _service.Bus;
        // There is no polling fallback for these notifications. An adapter that rejects the
        // subscription must fail opening the opted-in node rather than silently lose overruns.
        _controllerOverrunBus.ErrorFrameReceived += OnControllerOverrun;
    }

    private void StartControllerOverrunMonitoring()
    {
        if (_controllerOverrunBus is not null)
            Volatile.Write(ref _controllerOverrunObservation, new OverrunObservation());
    }

    private void InvalidateControllerOverrunMonitoring()
    {
        Volatile.Write(ref _controllerOverrunObservation, null);
        _controllerOverrunActive = false;
    }

    private void OnControllerOverrun(object? sender, ICanErrorInfo error)
    {
        if ((error.ControllerStatus & (CanControllerStatus.RxOverflow | CanControllerStatus.TxOverflow)) == 0) return;
        var observation = Volatile.Read(ref _controllerOverrunObservation);
        if (observation is null || Interlocked.Exchange(ref observation.Pending, 1) != 0) return;
        try
        {
            _actor.Post(() =>
            {
                // Release before processing: a notification arriving during this job can queue
                // the next one. At most one further job waits, even during a driver error storm.
                Volatile.Write(ref observation.Pending, 0);
                if (!ReferenceEquals(observation, _controllerOverrunObservation)) return;
                _controllerOverrunActive = true;
                ReportActiveControllerOverrun();
            });
        }
        catch (ObjectDisposedException)
        {
            // A captured callback may outlive the actor. Disposal already ended its observation.
        }
    }

    private void ReportActiveControllerOverrun()
    {
        if (_controllerOverrunActive)
            RaiseCommunicationError(CommunicationError.ControllerOverrun, 0, 0x8110);
    }

    internal void AcknowledgeControllerOverrun()
    {
        ThrowIfDisposed();
        RunOnActorAndWait(() =>
        {
            _controllerOverrunActive = false;
            EndCommunicationError(CommunicationError.ControllerOverrun);
        });
    }

    private void DisposeControllerOverrunMonitor()
    {
        InvalidateControllerOverrunMonitoring();
        if (_controllerOverrunBus is not { } bus) return;
        _controllerOverrunBus = null;
        try { bus.ErrorFrameReceived -= OnControllerOverrun; }
        catch (Exception error) when (error is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            // Keep normal adapter teardown failures from abandoning the remaining node cleanup,
            // but report them and let fatal failures propagate (as in the LSS reader).
            RaiseBackgroundException(error);
        }
    }
}
