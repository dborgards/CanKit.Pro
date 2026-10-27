using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

// #423 / FR-CO-011: optional 8120h is an observed controller-level cause. Bus-off and unknown
// readings do not prove that a previously observed passive condition has been repaired.
internal sealed partial class CanOpenNode
{
    private BusStateMonitor? _controllerEmcyMonitor;
    private bool _controllerErrorPassive;
    private bool _controllerEmcyStarted;

    private void InitializeControllerErrorMonitor()
    {
        if (!_options.EmitControllerErrorEmcy) return;
        _controllerEmcyMonitor = new BusStateMonitor(_service.Bus, _actor);
        _controllerEmcyMonitor.StateChanged += OnControllerEmcyStateChanged;
    }

    private void StartControllerErrorMonitoring()
    {
        if (_controllerEmcyMonitor is not { } monitor) return;
        _controllerEmcyStarted = true;
        ObserveControllerEmcyState(monitor.CurrentState);
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
        if (_controllerEmcyMonitor is not { } monitor) return;
        monitor.StateChanged -= OnControllerEmcyStateChanged;
        monitor.Dispose();
        _controllerEmcyMonitor = null;
    }
}
