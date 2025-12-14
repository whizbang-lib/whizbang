namespace Whizbang.Core;

/// <summary>
/// Receptors receive messages (commands) and produce responses (events).
/// They are stateless decision-making components that apply business rules
/// and emit events representing decisions made.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <typeparam name="TResponse">The type of response this receptor produces</typeparam>
/// <docs>core-concepts/receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Integration/DispatcherReceptorIntegrationTests.cs</tests>
public interface IReceptor<in TMessage, TResponse> {
  /// <summary>
  /// Handles a message, applies business logic, and returns a response.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The response representing the decision made</returns>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receive_ValidCommand_ShouldReturnTypeSafeResponseAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receive_EmptyItems_ShouldThrowExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receive_AsyncOperation_ShouldCompleteAsynchronouslyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receive_CalculatesTotal_ShouldSumItemPricesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receptor_ShouldBeStateless_NoPersistentStateAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:MultipleReceptors_SameMessageType_ShouldAllHandleAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receptor_TupleResponse_ShouldReturnMultipleEventsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs:Receptor_ArrayResponse_ShouldReturnDynamicNumberOfEventsAsync</tests>
  ValueTask<TResponse> HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Receptors receive messages (commands/events) without producing a typed response.
/// This is the zero-allocation pattern for command/event handling where only side effects matter.
/// Use this interface when you don't need to return a business result, enabling optimal performance.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <docs>core-concepts/receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs</tests>
public interface IReceptor<in TMessage> {
  /// <summary>
  /// Handles a message and performs side effects without returning a result.
  /// For synchronous operations, return ValueTask.CompletedTask for zero allocations.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A ValueTask representing the async operation (use CompletedTask for sync operations)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs:VoidReceptor_SynchronousCompletion_ShouldCompleteWithoutAllocationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs:VoidReceptor_AsynchronousCompletion_ShouldCompleteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs:VoidReceptor_Validation_ShouldThrowExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs:VoidReceptor_MultipleInvocations_ShouldBeStatelessAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs:VoidReceptor_CancellationToken_ShouldRespectCancellationAsync</tests>
  ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}
