namespace Whizbang.Core;

/// <summary>
/// Marker interface for queries - messages that represent a request for data.
/// Queries are processed by Receptors which return query results without changing state.
/// </summary>
/// <docs>messaging/commands-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/MessageKindTests.cs:Detect_ImplementsIQuery_ReturnsQueryAsync</tests>
public interface IQuery : IMessage {
}
