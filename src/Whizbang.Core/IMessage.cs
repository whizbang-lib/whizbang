namespace Whizbang.Core;

/// <summary>
/// Marker interface for all messages in the system (commands, events, queries, etc.).
/// Used for generic constraints and type safety in receptors, dispatchers, and lifecycle systems.
/// </summary>
/// <remarks>
/// This interface serves as the base for all message types:
/// <list type="bullet">
/// <item><description><see cref="ICommand"/> - Messages that represent intentions to change state</description></item>
/// <item><description><see cref="IEvent"/> - Messages that represent facts about state changes that have occurred</description></item>
/// <item><description>Custom message types - Any application-specific message</description></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/messages</docs>
public interface IMessage {
}
