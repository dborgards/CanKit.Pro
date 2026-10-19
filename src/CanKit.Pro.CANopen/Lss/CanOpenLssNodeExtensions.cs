using System;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// The local layer-settings state machine of a node (CiA 305 §8). Implemented by the nodes that
/// <see cref="CanOpen.OpenNode(CanKit.Abstractions.API.Can.ICanBus, byte, CanOpenNodeOptions?)"/>
/// creates. Not added to <see cref="ICanOpenNode"/>: a new member there would break every other
/// implementer.
/// </summary>
public interface ILssSlave
{
    /// <summary>
    /// Waiting or configuration (§8.2.1). Waiting when layer setting services are off.
    /// </summary>
    LssState State { get; }
}

/// <summary>Reads the layer-settings state of a node without widening <see cref="ICanOpenNode"/>.</summary>
public static class CanOpenLssNodeExtensions
{
    /// <summary>
    /// The LSS state of <paramref name="node"/>. A node this library did not create, and a node
    /// whose layer setting services are off, is waiting.
    /// </summary>
    public static LssState LssState(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node is ILssSlave slave ? slave.State : global::CanKit.Pro.CANopen.Lss.LssState.Waiting;
    }
}
