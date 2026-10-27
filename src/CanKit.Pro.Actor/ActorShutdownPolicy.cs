namespace CanKit.Pro.Actor
{
    /// <summary>Chooses which pending timers a <see cref="ProtocolActor"/> executes during shutdown.</summary>
    public enum ActorShutdownPolicy
    {
        /// <summary>
        /// Preserves the original behavior: finish accepted mailbox work, then execute due timers.
        /// Timers that are not yet due are discarded. Rearming after disposal is rejected.
        /// </summary>
        DrainDueTimers = 0,

        /// <summary>
        /// Finish accepted mailbox work and discard pending timers, including already due ones.
        /// A timer selected for execution before disposal may still complete. No new work is accepted.
        /// </summary>
        DiscardPendingTimers = 1,
    }
}
