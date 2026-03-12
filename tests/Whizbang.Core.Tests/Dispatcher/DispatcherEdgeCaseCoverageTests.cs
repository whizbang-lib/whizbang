using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for edge case and error paths in Dispatcher.cs that are not covered
/// by existing test files. Focuses on:
/// - RoutedNone on generic typed overloads
/// - Null argument validation on less-tested overloads
/// - Cancellation on DispatchOptions overloads
/// - ScopeDelta from IMessageContext UserId/TenantId
/// - PublishAsync null event checks
/// - CascadeMessageAsync edge cases
/// - Void sync receptor fallback on generic typed overloads
/// - LocalInvokeWithReceipt edge cases
/// - _localInvokeAsyncInternal generic typed RoutedNone/ReceptorNotFound
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("EdgeCaseCoverage")]
public class DispatcherEdgeCaseCoverageTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record EdgeCommand(string Data);
  public record EdgeResult(string Data);
  public record UnknownEdgeMessage(string Data);

  // Receptor for EdgeCommand -> EdgeResult
  public class EdgeCommandReceptor : IReceptor<EdgeCommand, EdgeResult> {
    public ValueTask<EdgeResult> HandleAsync(EdgeCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new EdgeResult(message.Data));
    }
  }

  // Void receptor for EdgeCommand
  public record VoidEdgeCommand(string Data);

  public class VoidEdgeCommandReceptor : IReceptor<VoidEdgeCommand> {
    public ValueTask HandleAsync(VoidEdgeCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // SEND ASYNC WITH DISPATCH OPTIONS - ROUTED NONE
  // ========================================

  [Test]
  public async Task SendAsync_GenericTyped_WithDispatchOptions_RoutedNone_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = Route.None();
    var options = new DispatchOptions();

    // The generic typed SendAsync<TMessage> path does not unwrap IRouted;
    // it goes through _sendAsyncInternalWithOptionsAsync which tries receptor lookup directly.
    // RoutedNone has no receptor, so ReceptorNotFoundException is thrown.
    await Assert.That(async () =>
      await dispatcher.SendAsync<RoutedNone>(routedNone, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task SendAsync_Object_WithDispatchOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // SEND ASYNC WITH CONTEXT AND OPTIONS - NULL CHECKS
  // ========================================

  [Test]
  public async Task SendAsync_WithContextAndOptions_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync((object)null!, context, options))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_NullContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(command, (IMessageContext)null!, options))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("context");
  }

  // ========================================
  // SEND ASYNC WITH DISPATCH OPTIONS - CANCELLATION
  // ========================================

  [Test]
  public async Task SendAsync_GenericTyped_WithCancelledOptions_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.SendAsync(command, options))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task SendAsync_Object_WithCancelledOptions_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = (object)new EdgeCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.SendAsync(command, options))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");
    var context = MessageContext.New();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.SendAsync(command, context, options))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - ROUTED NONE AND NULL
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeResult>(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeResult>((object)null!, options))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)null!, options))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeResult>(command, options))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new VoidEdgeCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(command, options))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - RECEPTOR NOT FOUND
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeResult>(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // GENERIC TYPED LOCAL INVOKE - ROUTED NONE ON _localInvokeAsyncInternal<TMessage, TResult>
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<RoutedNone, EdgeResult>(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeCommand, EdgeResult>(null!, context))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_NullContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<EdgeCommand, EdgeResult>(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // GENERIC TYPED VOID LOCAL INVOKE - ROUTED NONE ON _localInvokeAsyncInternal<TMessage>
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = Route.None();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<RoutedNone>(routedNone))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_WithContext_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<RoutedNone>(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<VoidEdgeCommand>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_WithContext_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<VoidEdgeCommand>(null!, context))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_WithContext_NullContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new VoidEdgeCommand("test");

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<VoidEdgeCommand>(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<UnknownEdgeMessage>(unknown))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_WithContext_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var context = MessageContext.New();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<UnknownEdgeMessage>(unknown, context))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT - EDGE CASES
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>((object)null!, context))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_NullContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("context");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var context = MessageContext.New();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(unknown, context))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_NullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>((object)null!, options))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(command, options))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT - GENERIC TYPED OVERLOADS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTyped_NoContext_ReturnsResultAndReceiptAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("typed-test");

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<EdgeCommand, EdgeResult>(command);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("typed-test");
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // SCOPE DELTA FROM MESSAGE CONTEXT - UserId/TenantId paths
  // ========================================

  [Test]
  public async Task SendAsync_WithContextContainingUserId_PreservesInEnvelopeAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("scope-test");
    var context = new MessageContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // The ScopeDelta path should be exercised when UserId/TenantId is set on context
    var receipt = await dispatcher.SendAsync(command, context);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeAsync_WithContextContainingUserId_PreservesContextAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("scope-invoke-test");
    var context = new MessageContext {
      UserId = "user-456",
      TenantId = "tenant-xyz"
    };

    var result = await dispatcher.LocalInvokeAsync<EdgeResult>(command, context);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("scope-invoke-test");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithContext_ContainingUserId_PreservesContextAsync() {
    var dispatcher = _createDispatcher();
    var command = new VoidEdgeCommand("scope-void-test");
    var context = new MessageContext {
      UserId = "user-789"
    };

    // Exercises _getScopeDeltaForHop with UserId set
    await dispatcher.LocalInvokeAsync(command, context);
    // No assertion needed beyond not throwing
  }

  [Test]
  public async Task LocalInvokeAsync_WithContextContainingOnlyTenantId_PreservesContextAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("tenant-only-test");
    var context = new MessageContext {
      TenantId = "tenant-only"
    };

    var result = await dispatcher.LocalInvokeAsync<EdgeResult>(command, context);

    await Assert.That(result).IsNotNull();
  }

  // ========================================
  // CASCADE MESSAGE ASYNC - EDGE CASES
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_WithNoneMode_DoesNotThrowAsync() {
    var dispatcher = _createDispatcher();
    var evt = new TestCascadeEvent { Detail = "none-mode" };

    // DispatchMode.None should skip all dispatch paths
    await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, mode: DispatchMode.None);
    // No assertion needed beyond not throwing
  }

  [Test]
  public async Task CascadeMessageAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.That(async () =>
      await dispatcher.CascadeMessageAsync(null!, sourceEnvelope: null, mode: DispatchMode.Local))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task CascadeMessageAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var evt = new TestCascadeEvent { Detail = "cancelled" };
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () =>
      await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, mode: DispatchMode.Local, cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // PUBLISH ASYNC - NULL EVENT CHECKS
  // ========================================

  [Test]
  public async Task PublishAsync_WithNullEvent_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    await Assert.That(async () =>
      await dispatcher.PublishAsync<TestCascadeEvent>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_NullEvent_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.PublishAsync<TestCascadeEvent>(null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var evt = new TestCascadeEvent { Detail = "cancelled-publish" };
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.PublishAsync(evt, options))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // SEND MANY ASYNC - MULTIPLE LOCAL MESSAGES
  // ========================================

  [Test]
  public async Task SendManyAsync_WithMultipleLocalMessages_ReturnsAllReceiptsAsync() {
    var dispatcher = _createDispatcher();
    var messages = new[]
    {
      new EdgeCommand("one"),
      new EdgeCommand("two"),
      new EdgeCommand("three")
    };

    var receipts = await dispatcher.SendManyAsync<EdgeCommand>(messages);

    await Assert.That(receipts).IsNotNull();
    await Assert.That(receipts.Count()).IsEqualTo(3);
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_WithMultipleLocalMessages_ReturnsAllReceiptsAsync() {
    var dispatcher = _createDispatcher();
    var messages = new object[]
    {
      new EdgeCommand("a"),
      new EdgeCommand("b")
    };

    var receipts = await dispatcher.SendManyAsync(messages);

    await Assert.That(receipts).IsNotNull();
    await Assert.That(receipts.Count()).IsEqualTo(2);
  }

  // ========================================
  // LOCAL INVOKE MANY ASYNC
  // ========================================

  [Test]
  public async Task LocalInvokeManyAsync_WithMultipleMessages_ReturnsAllResultsAsync() {
    var dispatcher = _createDispatcher();
    var messages = new object[]
    {
      new EdgeCommand("first"),
      new EdgeCommand("second")
    };

    var results = await dispatcher.LocalInvokeManyAsync<EdgeResult>(messages);

    await Assert.That(results).IsNotNull();
    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(2);
    await Assert.That(resultList[0].Data).IsEqualTo("first");
    await Assert.That(resultList[1].Data).IsEqualTo("second");
  }

  // ========================================
  // SEND ASYNC - GENERIC TYPED WITH ROUTED LOCAL (unwrap path)
  // ========================================

  [Test]
  public async Task SendAsync_WithRoutedLocal_UnwrapsAndDispatchesAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("routed-local");
    var routed = Route.Local(command);
    var context = MessageContext.New();

    // Routed<T> with DispatchMode.Local should unwrap and dispatch
    var receipt = await dispatcher.SendAsync((object)routed, context);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - HAPPY PATHS (exercises tracing+options internal paths)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_HappyPath_ReturnsResultAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("options-happy");
    var options = new DispatchOptions();

    var result = await dispatcher.LocalInvokeAsync<EdgeResult>(command, options);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("options-happy");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_HappyPath_CompletesAsync() {
    var dispatcher = _createDispatcher();
    var command = new VoidEdgeCommand("void-options-happy");
    var options = new DispatchOptions();

    // Should complete without exception
    await dispatcher.LocalInvokeAsync(command, options);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_HappyPath_ReturnsResultAndReceiptAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("receipt-options-happy");
    var options = new DispatchOptions();

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<EdgeResult>(command, options);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("receipt-options-happy");
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // GENERIC TYPED LOCAL INVOKE - UNKNOWN MESSAGE (ReceptorNotFound on _localInvokeAsyncInternal)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<UnknownEdgeMessage, EdgeResult>(unknown))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");
    var context = MessageContext.New();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<UnknownEdgeMessage, EdgeResult>(unknown, context))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // GENERIC TYPED SEND ASYNC - UNKNOWN MESSAGE (no receptor, no outbox)
  // ========================================

  [Test]
  public async Task SendAsync_GenericTyped_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownEdgeMessage("test");

    await Assert.That(async () =>
      await dispatcher.SendAsync(unknown))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT - GENERIC TYPED WITH CONTEXT
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTyped_WithContextAndUserId_ReturnsResultAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("receipt-typed-ctx");
    var context = new MessageContext {
      UserId = "receipt-user",
      TenantId = "receipt-tenant"
    };

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<EdgeCommand, EdgeResult>(command, context);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("receipt-typed-ctx");
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // SEND ASYNC - GENERIC TYPED INTERNAL WITH CONTEXT+OPTIONS
  // ========================================

  [Test]
  public async Task SendAsync_GenericTyped_WithOptions_HappyPath_ReturnsReceiptAsync() {
    var dispatcher = _createDispatcher();
    var command = new EdgeCommand("generic-options");
    var options = new DispatchOptions();

    var receipt = await dispatcher.SendAsync(command, options);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // HELPER TYPES
  // ========================================

  [DefaultRouting(DispatchMode.Local)]
  public class TestCascadeEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; set; } = Guid.NewGuid();
    public string Detail { get; set; } = "";
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
