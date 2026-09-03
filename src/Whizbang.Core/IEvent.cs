namespace Whizbang.Core;

/// <summary>
/// Marker interface for events - messages that represent facts about state changes that have occurred.
/// Events are emitted by Receptors and processed by Perspectives to update read models.
/// </summary>
/// <docs>messaging/commands-events</docs>
/// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:EventStoreContractTests</tests>
public interface IEvent : IMessage;
