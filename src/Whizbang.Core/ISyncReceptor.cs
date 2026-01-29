namespace Whizbang.Core;

/// <summary>
/// Synchronous receptor for messages that don't require async operations.
/// Use this when your receptor logic is pure computation without I/O.
/// The framework wraps the result in a pre-completed ValueTask for uniform handling.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <typeparam name="TResponse">The type of response this receptor produces</typeparam>
/// <docs>core-concepts/receptors#synchronous-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Receptors/SyncReceptorTests.cs:SyncReceptor_Handle_ReturnsTypedResponseAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Receptors/SyncReceptorTests.cs:SyncReceptor_TupleReturn_ReturnsMultipleValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Receptors/SyncReceptorTests.cs:SyncReceptor_Stateless_NoSharedStateAsync</tests>
public interface ISyncReceptor<in TMessage, out TResponse> {
  /// <summary>
  /// Handles a message synchronously and returns a response.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <returns>The response (may include events for auto-cascade)</returns>
  TResponse Handle(TMessage message);
}

/// <summary>
/// Synchronous receptor for messages without a typed response.
/// Use for side-effect-only operations that don't require async.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <docs>core-concepts/receptors#synchronous-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Receptors/SyncReceptorTests.cs:VoidSyncReceptor_Handle_ExecutesSynchronouslyAsync</tests>
public interface ISyncReceptor<in TMessage> {
  /// <summary>
  /// Handles a message synchronously without returning a result.
  /// </summary>
  /// <param name="message">The message to process</param>
  void Handle(TMessage message);
}
