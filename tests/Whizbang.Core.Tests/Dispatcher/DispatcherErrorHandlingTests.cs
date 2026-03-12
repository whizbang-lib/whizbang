using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for error handling paths in Dispatcher.cs.
/// Covers: null argument checks, RoutedNone (Route.None()) argument exceptions,
/// missing receptor (ReceptorNotFoundException), and cancellation.
/// </summary>
[Category("Dispatcher")]
[Category("ErrorHandling")]
public class DispatcherErrorHandlingTests {

  // ========================================
  // Test Message Types
  // ========================================

  public record SimpleCommand(string Data);
  public record SimpleResult(string Data);

  public class SimpleCommandReceptor : IReceptor<SimpleCommand, SimpleResult> {
    public ValueTask<SimpleResult> HandleAsync(SimpleCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new SimpleResult(message.Data));
    }
  }

  public record VoidSimpleCommand(string Data);

  public class VoidSimpleCommandReceptor : IReceptor<VoidSimpleCommand> {
    public ValueTask HandleAsync(VoidSimpleCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // NULL ARGUMENT TESTS - SendAsync
  // ========================================

  [Test]
  public async Task SendAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.That(async () => await dispatcher.SendAsync((object)null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task SendAsync_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("test");

    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("context");
  }

  // ========================================
  // NULL ARGUMENT TESTS - LocalInvokeAsync
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>((object)null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_WithNullMessage_AndContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>((object)null!, context))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithNullMessage_AndContext_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)null!, context))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("message");
  }

  // ========================================
  // ROUTED NONE / ROUTE.NONE() ARGUMENT EXCEPTION TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();

    var ex = await Assert.That(async () => await dispatcher.SendAsync(routedNone))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task SendAsync_WithContext_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () => await dispatcher.SendAsync(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
    await Assert.That(ex.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(routedNone, context, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>(routedNone))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_WithContext_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
    await Assert.That(ex.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(routedNone))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithContext_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
    await Assert.That(ex.ParamName).IsEqualTo("message");
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(routedNone, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // RECEPTOR NOT FOUND TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithNoReceptorAndNoOutbox_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    // Use a type that has no registered receptor
    var unknown = new UnknownMessage("test");

    await Assert.That(async () => await dispatcher.SendAsync(unknown))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_NoReceptor_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<SimpleResult>(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_NoReceptor_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NoReceptor_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownMessage("test");

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>(unknown))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_NoReceptor_ThrowsReceptorNotFoundExceptionAsync() {
    var dispatcher = _createDispatcher();
    var unknown = new UnknownMessage("test");
    var options = new DispatchOptions();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>(unknown, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // CANCELLATION TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithCancelledOptions_ThrowsOperationCanceledExceptionAsync() {
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>(command, options))
      .Throws<OperationCanceledException>();
  }

  // ========================================
  // LocalInvokeWithReceiptAsync HAPPY PATH TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_ReturnsResultAndReceiptAsync() {
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("hello");
    var options = new DispatchOptions();

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>(command, options);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("hello");
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_Generic_WithContext_ReturnsResultAndReceiptAsync() {
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("context-test");
    var correlationId = CorrelationId.New();
    var context = MessageContext.Create(correlationId);

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<SimpleCommand, SimpleResult>(
      command, context);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("context-test");
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NonGeneric_NoContext_ReturnsResultAsync() {
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("nongeneric");

    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>((object)command);

    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.Data).IsEqualTo("nongeneric");
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();
    var routedNone = (object)Route.None();
    var context = MessageContext.New();

    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<SimpleResult>(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // SendManyAsync EDGE CASES
  // ========================================

  [Test]
  public async Task SendManyAsync_WithEmptyCollection_ReturnsEmptyReceiptsAsync() {
    var dispatcher = _createDispatcher();
    var empty = Array.Empty<object>();

    var receipts = await dispatcher.SendManyAsync(empty);

    await Assert.That(receipts).IsNotNull();
    await Assert.That(receipts.Count()).IsEqualTo(0);
  }

  [Test]
  public async Task SendManyAsync_Generic_WithEmptyCollection_ReturnsEmptyReceiptsAsync() {
    var dispatcher = _createDispatcher();
    var empty = Array.Empty<SimpleCommand>();

    var receipts = await dispatcher.SendManyAsync<SimpleCommand>(empty);

    await Assert.That(receipts).IsNotNull();
    await Assert.That(receipts.Count()).IsEqualTo(0);
  }

  [Test]
  public async Task SendManyAsync_WithNullCollection_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    await Assert.That(async () => await dispatcher.SendManyAsync((IEnumerable<object>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendManyAsync_Generic_WithNullCollection_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    await Assert.That(async () => await dispatcher.SendManyAsync<SimpleCommand>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeManyAsync_WithNullCollection_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = _createDispatcher();

    await Assert.That(async () =>
      await dispatcher.LocalInvokeManyAsync<SimpleResult>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeManyAsync_WithEmptyCollection_ReturnsEmptyResultsAsync() {
    var dispatcher = _createDispatcher();
    var empty = Array.Empty<object>();

    var results = await dispatcher.LocalInvokeManyAsync<SimpleResult>(empty);

    await Assert.That(results).IsNotNull();
    await Assert.That(results.Count()).IsEqualTo(0);
  }

  // ========================================
  // Helper Types
  // ========================================

  /// <summary>
  /// Message type with no registered receptor - triggers ReceptorNotFoundException.
  /// </summary>
  public record UnknownMessage(string Data);

  // ========================================
  // Helper Methods
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
