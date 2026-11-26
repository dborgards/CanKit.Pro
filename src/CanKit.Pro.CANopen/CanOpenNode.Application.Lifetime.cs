using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    // Written during attach. Published before the node can be disposed, or read during unwind
    // on the constructing thread. A profile may dispose its own node inside a callback. Closing
    // admission and counting active callbacks avoids waiting for ourselves or a dictionary writer.
    private List<IDisposable>? _attachmentResources;
    private bool _applicationsEnded;
    private int _activeApplicationCalls;

    private bool EnterApplicationCall()
    {
        lock (_applicationGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _applicationPostsClosed)) return false;
            _activeApplicationCalls++;
            return true;
        }
    }

    private void LeaveApplicationCall()
    {
        lock (_applicationGate)
        {
            if (--_activeApplicationCalls != 0 || !_applicationsEnded || _attachmentResources is null) return;
        }
        // The last callback can be ValidateWrite, still inside the dictionary write gate. A
        // resource may join a worker that needs that gate, so run late cleanup off this stack.
        _ = Task.Run(ReleaseAttachmentResources);
    }

    private void EndApplications()
    {
        lock (_applicationGate)
        {
            _applicationsEnded = true;
            if (_activeApplicationCalls != 0) return;
        }
        ReleaseAttachmentResources();
    }

    private void ReleaseAttachmentResources()
    {
        var resources = Interlocked.Exchange(ref _attachmentResources, null);
        if (resources is null) return;
        Callbacks.Enter(CallbackKind.Finishing, this);
        try
        {
            for (int i = resources.Count - 1; i >= 0; i--)
                DisposeReportingFailure(resources[i]);
        }
        finally
        {
            Callbacks.Leave(CallbackKind.Finishing, this);
        }
    }
}
