using System;
using System.Threading.Tasks;

namespace CanKit.Pro.Actor
{
    /// <summary>
    /// A protocol instance's documented threading model (arc42 §8.3, ADR-6; FR-RAW-020..024):
    /// exactly one mailbox, processed by exactly one logical loop, so a protocol instance's
    /// internal state (channel registers, timer queues, state machines) is never mutated from
    /// more than one place at a time — no locks needed by the protocol code itself
    /// (FR-RAW-020/021). Scheduling is event-driven, never a busy loop (FR-RAW-022); background
    /// exceptions are surfaced via <see cref="BackgroundExceptionOccurred"/> instead of being
    /// thrown on some unrelated caller thread or lost as an unobserved task exception
    /// (FR-RAW-023).
    /// </summary>
    public interface IProtocolActor : IDisposable
    {
        /// <summary>
        /// Enqueues <paramref name="work"/> to run on the actor's mailbox loop and returns
        /// immediately ("tell" / fire-and-forget). If <paramref name="work"/> throws, the
        /// exception is caught by the loop and surfaced via
        /// <see cref="BackgroundExceptionOccurred"/> — there is no other way for a fire-and-forget
        /// caller to observe it (FR-RAW-023).
        /// </summary>
        void Post(Action work);

        /// <summary>
        /// Enqueues <paramref name="work"/> to run on the actor's mailbox loop and returns a task
        /// that completes once it has run ("ask"). Unlike <see cref="Post"/>, an exception from
        /// <paramref name="work"/> is surfaced through the returned task's fault, not through
        /// <see cref="BackgroundExceptionOccurred"/> — the caller is already positioned to observe
        /// it by awaiting.
        /// </summary>
        Task PostAsync(Action work);

        /// <summary>
        /// Same as <see cref="PostAsync(Action)"/> but returns <paramref name="work"/>'s result.
        /// </summary>
        Task<T> PostAsync<T>(Func<T> work);

        /// <summary>
        /// Schedules <paramref name="callback"/> to run on the actor's mailbox loop once
        /// <paramref name="delay"/> has elapsed, using event-driven waiting rather than polling
        /// (FR-RAW-022) — suitable for STmin waits, timeout checks, and similar periodic/timed
        /// protocol tasks. Disposing the returned handle cancels the callback on a best-effort
        /// basis: it will not fire if cancellation is observed before it becomes due, but a
        /// callback already in flight on the loop may still complete.
        /// </summary>
        IDisposable Schedule(TimeSpan delay, Action callback);

        /// <summary>
        /// Raised whenever a posted work item (via <see cref="Post"/>) or a scheduled callback
        /// (via <see cref="Schedule"/>) throws — the actor's single, defined channel for
        /// background exceptions (FR-RAW-023). The mailbox loop keeps running afterward; one
        /// failing item never stops the actor. Never raised for <see cref="PostAsync(Action)"/>/
        /// <see cref="PostAsync{T}(Func{T})"/> failures, which surface through their own returned
        /// task instead.
        /// </summary>
        event EventHandler<Exception> BackgroundExceptionOccurred;
    }
}
