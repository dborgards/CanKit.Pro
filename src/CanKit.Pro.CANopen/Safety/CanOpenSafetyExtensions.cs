using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Reaches the safety layer of a node without widening <see cref="ICanOpenNode"/>
/// (adding members to a published interface breaks every implementer; see the DisposeAsync
/// extension for the precedent).</summary>
public static class CanOpenSafetyExtensions
{
    /// <summary>The <see cref="ICanOpenSafety"/> of a node this library created.</summary>
    /// <exception cref="NotSupportedException">The node is not one of this library's.</exception>
    public static ICanOpenSafety Safety(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node as ICanOpenSafety
            ?? throw new NotSupportedException("CANopen Safety is available on the nodes CanOpen.OpenNode creates.");
    }
}
