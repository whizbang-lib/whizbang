using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Sagas.Helpers;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for <see cref="SagaCompletionGuard.EmitOnceAsync{TEvent}"/> — the sibling
/// <see cref="SagaCompletionGuardTests"/> suite locks <see cref="SagaCompletionGuard.ClaimKey"/>
/// only. Pure delegation to <see cref="IDispatcher.PublishOnceAsync{TEvent}"/>, no database.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/Helpers/SagaCompletionGuard.cs</code-under-test>
public class SagaCompletionGuardCoverageTests {

  /// <summary>A test-only event to drive the dispatcher's generic surface.</summary>
  public sealed class TestEvent : IEvent {
    [StreamId] public Guid EntityId { get; set; }
    public Guid MessageId { get; set; } = TrackedGuid.NewMedo().Value;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
  }

  // Two receptors racing on the same saga's terminal state both call EmitOnceAsync; if it stopped
  // routing through the dispatcher's claim-key path (or built the wrong key), both could win their
  // own claim and the saga's completion event would fire twice — a downstream consumer would see
  // the same completion notification more than once.
  [Test]
  public async Task EmitOnceAsync_DelegatesToDispatcherPublishOnceWithTheConventionKeyAsync() {
    var dispatcher = new _recordingDispatcher();
    var sagaId = Guid.CreateVersion7();
    var evt = new TestEvent();

    var result = await SagaCompletionGuard.EmitOnceAsync(dispatcher, "OrderSaga", sagaId, evt, CancellationToken.None);

    await Assert.That(dispatcher.PublishOnceCallCount).IsEqualTo(1)
      .Because("EmitOnceAsync must route through IDispatcher.PublishOnceAsync exactly once, never a plain PublishAsync");
    await Assert.That(dispatcher.LastClaimKey).IsEqualTo(SagaCompletionGuard.ClaimKey("OrderSaga", sagaId))
      .Because("the claim key must match the ClaimKey convention exactly — a mismatched key would let a racing receptor win its own separate claim");
    await Assert.That(dispatcher.LastEvent).IsSameReferenceAs(evt);
    await Assert.That(result).IsTrue();
  }

  // A null dispatcher must fail at the call site that forgot to wire one up, not as an NRE deep
  // inside the framework's publish-once machinery.
  [Test]
  public async Task EmitOnceAsync_NullDispatcher_ThrowsArgumentNullAsync() {
    await Assert.That(() => SagaCompletionGuard.EmitOnceAsync(
        null!, "OrderSaga", Guid.CreateVersion7(), new TestEvent(), CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Hand-rolled IDispatcher fake. Only PublishOnceAsync is real — EmitOnceAsync calls nothing
  /// else — so every other member throws if exercised, catching any future regression that
  /// accidentally routes through the wrong dispatcher surface.
  /// </summary>
  private sealed class _recordingDispatcher : IDispatcher {
    public int PublishOnceCallCount { get; private set; }
    public string? LastClaimKey { get; private set; }
    public object? LastEvent { get; private set; }

    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) {
      PublishOnceCallCount++;
      LastClaimKey = claimKey;
      LastEvent = eventData;
      return Task.FromResult(true);
    }

    // Everything else — throws if exercised.
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) => _ns();
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => _ns();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => _ns();
    public Task<IDeliveryReceipt> SendAsync(object message) => _ns();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _ns();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => _ns();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => _ns();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _ns();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => _nsVt<TResult>();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => _nsVt<TResult>();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVt<TResult>();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVt<TResult>();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => _nsVtVoid();
    public ValueTask LocalInvokeAsync(object message) => _nsVtVoid();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVtVoid();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVtVoid();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => _nsVt<TResult>();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => _nsVtVoid();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => _nsVt<InvokeResult<TResult>>();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => _nsVt<InvokeResult<TResult>>();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull => _nsVt<InvokeResult<TResult>>();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) => _nsVt<InvokeResult<TResult>>();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => _nsVt<InvokeResult<TResult>>();
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => _ns<IEnumerable<IDeliveryReceipt>>();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => _ns<IEnumerable<IDeliveryReceipt>>();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => _nsVt<IEnumerable<IDeliveryReceipt>>();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => _nsVt<IEnumerable<IDeliveryReceipt>>();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => _ns<IEnumerable<IDeliveryReceipt>>();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => _ns<IEnumerable<IDeliveryReceipt>>();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => _nsVt<IEnumerable<TResult>>();

    private static Task<IDeliveryReceipt> _ns() => throw new NotSupportedException("Method not exercised by these tests.");
    private static Task<T> _ns<T>() => throw new NotSupportedException("Method not exercised by these tests.");
    private static ValueTask<T> _nsVt<T>() => throw new NotSupportedException("Method not exercised by these tests.");
    private static ValueTask _nsVtVoid() => throw new NotSupportedException("Method not exercised by these tests.");
  }
}
