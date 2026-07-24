using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Error-branch coverage for Dispatcher.cs entry-point validation on the
/// IMessageContext-taking overloads (the DispatchOptions overloads are covered by
/// DispatcherEdgeCaseCoverageTests / DispatcherCoverageWave3Tests):
/// <list type="bullet">
/// <item><description>SendAsync(object, IMessageContext) RoutedNone rejection</description></item>
/// <item><description>SendAsync(object, IMessageContext, DispatchOptions) RoutedNone rejection + pre-cancelled token</description></item>
/// <item><description>LocalInvokeWithReceiptAsync&lt;TResult&gt;(object, IMessageContext) RoutedNone rejection</description></item>
/// <item><description>Routed&lt;T&gt; unwrap on SendAsync / LocalInvokeAsync context overloads (happy path
/// proving the unwrap hands the INNER message to the receptor)</description></item>
/// </list>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherErrorBranchTests {

  // ========================================
  // TEST MESSAGE TYPES + RECEPTORS
  // ========================================

  public record ErrorBranchCommand(string Data);
  public record ErrorBranchResult(string Data);

  public class ErrorBranchCommandReceptor : IReceptor<ErrorBranchCommand, ErrorBranchResult> {
    public ValueTask<ErrorBranchResult> HandleAsync(ErrorBranchCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new ErrorBranchResult(message.Data));
    }
  }

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  // ========================================
  // SendAsync(object, IMessageContext) — RoutedNone
  // ========================================

  [Test]
  public async Task SendAsync_ContextOverload_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
      await dispatcher.SendAsync((object)Route.None(), MessageContext.New()));

    await Assert.That(ex!.Message).Contains("RoutedNone")
      .Because("Route.None() carries no inner message — the context overload must reject it with the standard diagnostic.");
  }

  [Test]
  public async Task SendAsync_ContextAndOptionsOverload_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
      await dispatcher.SendAsync((object)Route.None(), MessageContext.New(), new DispatchOptions()));

    await Assert.That(ex!.Message).Contains("RoutedNone")
      .Because("The context+options overload performs the same RoutedNone validation as the plain context overload.");
  }

  [Test]
  public async Task SendAsync_ContextAndOptionsOverload_PreCancelledToken_ThrowsBeforeValidationAsync() {
    var dispatcher = _createDispatcher();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    // Even a null message must NOT produce ArgumentNullException here — cancellation is
    // checked FIRST, so callers get a consistent OCE on shutdown regardless of arguments.
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await dispatcher.SendAsync((object)null!, MessageContext.New(), options));
  }

  // ========================================
  // LocalInvokeWithReceiptAsync(object, IMessageContext) — RoutedNone
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_ContextOverload_RoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = _createDispatcher();

    var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<ErrorBranchResult>((object)Route.None(), MessageContext.New()));

    await Assert.That(ex!.Message).Contains("RoutedNone")
      .Because("Receipt-returning invokes must reject RoutedNone with the same diagnostic as plain invokes.");
  }

  // ========================================
  // Routed<T> unwrap — happy path through the context overloads
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_ContextOverload_RoutedLocal_UnwrapsAndInvokesReceptorAsync() {
    var dispatcher = _createDispatcher();
    var wrapped = (object)Route.Local(new ErrorBranchCommand("unwrap-local-invoke"));

    var result = await dispatcher.LocalInvokeAsync<ErrorBranchResult>(wrapped, MessageContext.New());

    await Assert.That(result.Data).IsEqualTo("unwrap-local-invoke")
      .Because("LocalInvokeAsync must unwrap Routed<T> and hand the INNER command to the receptor — dispatching the wrapper itself would find no receptor.");
  }

  [Test]
  public async Task SendAsync_ContextOverload_RoutedLocal_UnwrapsAndDeliversAsync() {
    var dispatcher = _createDispatcher();
    var wrapped = (object)Route.Local(new ErrorBranchCommand("unwrap-send"));

    var receipt = await dispatcher.SendAsync(wrapped, MessageContext.New());

    await Assert.That(receipt).IsNotNull()
      .Because("SendAsync must unwrap Routed<T>, find the inner command's receptor, and produce a delivery receipt.");
  }
}
