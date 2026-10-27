using System;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

// #423 / FR-CO-011: optional 8120h is an observed controller-level cause. Bus-off and unknown
// readings do not prove that a previously observed passive condition has been repaired.
internal sealed partial class CanOpenNode
{
    private BusStateMonitor? _controllerEmcyMonitor;
    private bool _controllerErrorPassive;
    private bool _controllerEmcyStarted;
    private IDisposable? _controllerBaselineRetry;
    private long _controllerBaselineGeneration;

    private void InitializeControllerErrorMonitor()
    {
        if (!_options.EmitControllerErrorEmcy) return;
        _controllerEmcyMonitor = new BusStateMonitor(_service.Bus, _actor);
        _controllerEmcyMonitor.StateChanged += OnControllerEmcyStateChanged;
    }

    private void InvalidateControllerErrorBaseline()
    {
        _controllerEmcyStarted = false;
        _controllerBaselineGeneration++;
        _controllerBaselineRetry?.Dispose();
        _controllerBaselineRetry = null;
    }

    private void StartControllerErrorMonitoring()
    {
        if (_controllerEmcyMonitor is null) return;
        InvalidateControllerErrorBaseline();
        long generation = _controllerBaselineGeneration;
        // Diagnostic reads are a separate actor job. Even an unexpected adapter exception must
        // not interrupt the reset caller's echo bookkeeping or an LSS configure-node-id reply.
        _actor.Post(() => RefreshControllerErrorBaseline(generation));
    }

    private void RefreshControllerErrorBaseline(long generation)
    {
        if (_controllerEmcyMonitor is null) return;
        if (generation != _controllerBaselineGeneration) return;
        _controllerBaselineRetry?.Dispose();
        _controllerBaselineRetry = null;
        _controllerEmcyStarted = false;

        // A reset starts a new error history. Refresh both the physical sample and the monitor's
        // comparison baseline: reusing CurrentState could replay a repaired pre-reset lapse, and
        // sampling only the bus could hide the next passive edge behind the monitor's old cache.
        BusStateMonitor fresh;
        try
        {
            fresh = new BusStateMonitor(_service.Bus, _actor);
        }
        catch (Exception ex) when (ex is CanKitException or ObjectDisposedException)
        {
            // A transient status-read failure must neither invent a fresh error nor remove the
            // existing monitor. Retry on the same actor clock; normal teardown cancels this work.
            RaiseBackgroundException(ex);
            _controllerBaselineRetry = _actor.Schedule(System.TimeSpan.FromMilliseconds(50), () => RefreshControllerErrorBaseline(generation));
            return;
        }
        fresh.StateChanged += OnControllerEmcyStateChanged;
        DisposeControllerErrorMonitor();
        _controllerEmcyMonitor = fresh;
        _controllerErrorPassive = false;
        _controllerEmcyStarted = true;
        ObserveControllerEmcyState(fresh.CurrentState);
    }

    private void OnControllerEmcyStateChanged(object? sender, BusStateChangedEventArgs e)
        => ObserveControllerEmcyState(e.Current);

    private void ObserveControllerEmcyState(BusState state)
    {
        switch (state)
        {
            case BusState.ErrPassive:
                _controllerErrorPassive = true;
                break;
            case BusState.None:
            case BusState.ErrActive:
            case BusState.ErrWarning:
                _controllerErrorPassive = false;
                EndCommunicationError(CommunicationError.ControllerPassive);
                return;
        }
        ReportActiveControllerError();
    }

    private void ReportActiveControllerError()
    {
        if (_controllerEmcyStarted && _controllerErrorPassive)
            RaiseCommunicationError(CommunicationError.ControllerPassive, 0, 0x8120);
    }

    private void DisposeControllerErrorMonitor()
    {
        _controllerBaselineRetry?.Dispose();
        _controllerBaselineRetry = null;
        if (_controllerEmcyMonitor is not { } monitor) return;
        monitor.StateChanged -= OnControllerEmcyStateChanged;
        monitor.Dispose();
        _controllerEmcyMonitor = null;
    }
}
