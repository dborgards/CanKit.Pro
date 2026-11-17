using System;
using System.Collections.Generic;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

// The application seam: device profiles that attach to the node when it opens
// (CanOpenNodeOptions.Applications). Attaching, the claims on object indices, and the host the
// node gives each application live here.
internal sealed partial class CanOpenNode
{
    // The lowest index an application may claim: below it lie the communication profile and the
    // reserved areas, which belong to the node.
    private const ushort FirstClaimableIndex = 0x2000;

    // One per attached application, in attach order. Written once, on the constructing thread,
    // before the node's actor runs anything; empty for a node opened without applications.
    private ApplicationSlot[] _applications = Array.Empty<ApplicationSlot>();

    private sealed class ApplicationSlot
    {
        internal ApplicationSlot(ICanOpenApplication application, ApplicationHost host)
        {
            Application = application;
            Host = host;
        }

        internal ICanOpenApplication Application { get; }

        internal ApplicationHost Host { get; }

        // Copied when the node opens: the claims are read once, so a list the application changes
        // later cannot move what the node enforces.
        internal CanOpenIndexRange[] Claims { get; init; } = Array.Empty<CanOpenIndexRange>();
    }

    /// <summary>Whether an attached application claims <paramref name="index"/>.</summary>
    private bool IsClaimed(ushort index) => ClaimantOf(index) is not null;

    /// <summary>The application that claims <paramref name="index"/>, or <see langword="null"/>.</summary>
    private ApplicationSlot? ClaimantOf(ushort index)
    {
        var applications = _applications;
        for (int i = 0; i < applications.Length; i++)
        {
            foreach (var range in applications[i].Claims)
            {
                if (range.Contains(index)) return applications[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Validates the claims and requirements of <see cref="CanOpenNodeOptions.Applications"/>, then
    /// attaches each application in list order. Runs in the constructor, inside the guard that ends
    /// the actor on a throw, after the dictionary holds the communication profile and the device
    /// description, and before the node boots.
    /// </summary>
    /// <exception cref="ArgumentException">A claim or requirement cannot be met; the parameter is
    /// <c>options</c> and the message names the application.</exception>
    private void AttachApplications()
    {
        var listed = _options.Applications;
        if (listed.Count == 0) return;

        var slots = new ApplicationSlot[listed.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var application = listed[i] ?? throw OptionsError($"CanOpenNodeOptions.Applications[{i}] is null.");
            for (int earlier = 0; earlier < i; earlier++)
            {
                if (ReferenceEquals(listed[earlier], application))
                    throw OptionsError($"Application '{application.Name}' is listed twice in CanOpenNodeOptions.Applications.");
            }
            slots[i] = new ApplicationSlot(application, new ApplicationHost(this))
            {
                Claims = ValidatedClaims(application, slots, i),
            };
            ValidateRequirements(application);
        }

        _applications = slots;
        foreach (var slot in slots) slot.Application.Attach(slot.Host);

        // What the applications declared is part of the power-on state, like what the description
        // declared: Reset Node returns it to this, and 1010h saves it.
        _factoryDefaults = SnapshotRestorableValues();
        _powerOnValues = _factoryDefaults;
    }

    private static CanOpenIndexRange[] ValidatedClaims(ICanOpenApplication application, ApplicationSlot[] earlier, int count)
    {
        var claims = new CanOpenIndexRange[application.ClaimedRanges.Count];
        for (int r = 0; r < claims.Length; r++)
        {
            var range = application.ClaimedRanges[r];
            if (range.First < FirstClaimableIndex)
            {
                throw OptionsError($"Application '{application.Name}' claims {range}, which reaches below 0x{FirstClaimableIndex:X4}: " +
                    "the communication profile and the reserved areas belong to the node.");
            }
            for (int other = 0; other < r; other++)
            {
                if (range.Overlaps(claims[other]))
                    throw OptionsError($"Application '{application.Name}' claims {claims[other]} and {range}, which overlap.");
            }
            for (int e = 0; e < count; e++)
            {
                foreach (var taken in earlier[e].Claims)
                {
                    if (range.Overlaps(taken))
                    {
                        throw OptionsError($"Applications '{earlier[e].Application.Name}' and '{application.Name}' claim overlapping " +
                            $"ranges: {taken} and {range}.");
                    }
                }
            }
            claims[r] = range;
        }
        return claims;
    }

    private void ValidateRequirements(ICanOpenApplication application)
    {
        var requirements = application.Requirements;
        if (requirements.PdoCount > _pdoCount)
        {
            throw OptionsError($"Application '{application.Name}' configures {requirements.PdoCount} PDOs: " +
                $"set CanOpenNodeOptions.PdoCount to at least {requirements.PdoCount} (it is {_pdoCount}).");
        }
        if (requirements.CommunicationFailureMonitoring && !_options.EnableErrorBehavior)
        {
            throw OptionsError($"Application '{application.Name}' monitors communication failures: " +
                "set CanOpenNodeOptions.EnableErrorBehavior to true.");
        }
    }

    private static ArgumentException OptionsError(string message) => new(message, "options");

    /// <summary>What one application is given of the node. Members other than the dictionary, the
    /// state, the clock and the deadlines arrive with the callbacks that need them.</summary>
    private sealed class ApplicationHost : ICanOpenApplicationHost
    {
        private readonly CanOpenNode _node;

        internal ApplicationHost(CanOpenNode node) => _node = node;

        public ObjectDictionary ObjectDictionary => _node._od;

        public NmtState State => _node._state;

        public TimeSpan Now
        {
            get
            {
                var time = _node._actor.TimeSource;
                return TickMath.ElapsedFromTicks(time.GetTimestamp(), time.Frequency);
            }
        }

        public bool CommunicationFailureActive => throw new NotSupportedException();

        public IDeadline Arm(TimeSpan delay, Action onExpired) => _node._deadlines.Arm(delay, onExpired);

        public void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType) => throw new NotSupportedException();

        public void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries) => throw new NotSupportedException();

        public void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default)
            => throw new NotSupportedException();

        public void ClearError(ushort errorCode) => throw new NotSupportedException();

        public void RequestNmtState(NmtState target) => throw new NotSupportedException();
    }
}
