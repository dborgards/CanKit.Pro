namespace CanKit.Pro.Actor
{
    /// <summary>
    /// Chooses the execution context a <see cref="ProtocolActor"/> runs its single mailbox loop
    /// on (arc42 §8.3/ADR-6; FR-RAW-024).
    /// </summary>
    public enum ActorExecutionMode
    {
        /// <summary>
        /// Default: a genuine dedicated <see cref="System.Threading.Thread"/>, reserved for the
        /// lifetime of the actor. Every posted work item and timer callback demonstrably runs on
        /// that exact thread, never hops to another one across calls. Best for protocol instances
        /// with real-time-ish timing needs (STmin, N_As/N_Bs) where predictable scheduling
        /// matters more than the cost of an extra OS thread.
        /// </summary>
        DedicatedThread,

        /// <summary>
        /// The mailbox loop runs as a normal <see cref="System.Threading.Tasks.Task"/> on the
        /// .NET thread pool. Still strictly single-writer (never two work items execute
        /// concurrently), but successive items are not guaranteed to run on the same OS thread.
        /// Cheaper than <see cref="DedicatedThread"/> when many short-lived protocol instances
        /// exist simultaneously.
        /// </summary>
        ThreadPool,

        /// <summary>
        /// Every posted work item and timer callback is marshaled onto a caller-supplied
        /// <see cref="System.Threading.SynchronizationContext"/> (e.g. a UI dispatcher) via a
        /// blocking <see cref="System.Threading.SynchronizationContext.Send"/> call, so protocol
        /// callbacks can safely touch UI-bound state without the caller manually marshaling, and
        /// so that work is guaranteed to have actually run by the time it is considered processed
        /// (including during <see cref="ProtocolActor.Dispose"/>'s final drain). Requires passing a
        /// non-null context to <see cref="ProtocolActor(ActorExecutionMode, System.Threading.SynchronizationContext?)"/>.
        /// <b>Do not call <see cref="ProtocolActor.Dispose"/> synchronously from the actor's own
        /// target context thread</b> (e.g. from inside a UI event handler on that same dispatcher)
        /// — like any synchronous wait on work that needs that same thread to run, it can
        /// deadlock; dispose from a different thread, or dispatch the call asynchronously.
        /// </summary>
        SynchronizationContext,
    }
}
