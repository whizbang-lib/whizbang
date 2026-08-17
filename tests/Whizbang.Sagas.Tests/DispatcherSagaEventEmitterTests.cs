using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the bridge between <see cref="ISagaEventEmitter"/> and
/// <see cref="IDispatcher"/> — specifically that
/// <c>PublishAsync(TEvent, DateTimeOffset?)</c> with a non-null scheduledFor
/// routes through the dispatcher's options overload with
/// <see cref="DispatchOptions.ScheduledFor"/> populated, and that null
/// scheduledFor falls back to the simple PublishAsync path so the immediate-
/// dispatch contract is preserved verbatim.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class DispatcherSagaEventEmitterTests {

  /// <summary>A test-only event to drive the dispatcher's generic surface.</summary>
  public sealed class TestEvent : IEvent {
    [StreamId] public Guid EntityId { get; set; }
    public Guid MessageId { get; set; } = TrackedGuid.NewMedo().Value;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
  }

  [Test]
  public async Task PublishAsync_WithScheduledFor_RoutesThroughOptionsOverloadAsync() {
    var fireAt = DateTimeOffset.UtcNow.AddMinutes(7);
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent(), fireAt);

    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(1)
      .Because("Non-null scheduledFor must route through PublishAsync(event, DispatchOptions).");
    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(0)
      .Because("The simple PublishAsync overload must NOT be called when scheduledFor is non-null.");
    await Assert.That(dispatcher.LastCapturedOptions).IsNotNull();
    await Assert.That(dispatcher.LastCapturedOptions!.ScheduledFor)
      .IsEqualTo((DateTimeOffset?)fireAt)
      .Because("DispatchOptions.ScheduledFor must equal the value supplied to the emitter.");
  }

  [Test]
  public async Task PublishAsync_WithNullScheduledFor_FallsBackToImmediatePathAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent(), scheduledFor: null);

    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(1)
      .Because("Null scheduledFor means immediate dispatch — go through the simple PublishAsync overload.");
    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(0)
      .Because("Don't pay for the options object allocation when scheduledFor is null.");
  }

  [Test]
  public async Task PublishAsync_NoSchedulingOverload_DelegatesToDispatcherAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent());

    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(1);
    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task PublishOnceAsync_ForwardsClaimKeyAndEventToDispatcherAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);
    var key = "saga-completed:bulk-job:abc";
    var evt = new TestEvent();

    var result = await emitter.PublishOnceAsync(key, evt, CancellationToken.None);

    await Assert.That(dispatcher.PublishOnceCallCount).IsEqualTo(1)
      .Because("PublishOnceAsync must route through IDispatcher.PublishOnceAsync exactly.");
    await Assert.That(dispatcher.LastPublishOnceClaimKey).IsEqualTo(key);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Constructor_NullDispatcher_ThrowsAsync() {
    DispatcherSagaEventEmitter? _ = null;
    await Assert.That(() => _ = new DispatcherSagaEventEmitter(null!)).ThrowsExactly<ArgumentNullException>();
  }

  /// <summary>
  /// Hand-rolled IDispatcher fake. Only the two PublishAsync overloads + PublishOnceAsync are
  /// real; every other dispatcher method throws NotSupportedException so any future regression
  /// that accidentally routes through the wrong surface fails loudly. Records call counts and
  /// captures the last DispatchOptions / claim key for assertion.
  /// </summary>
  private sealed class RecordingDispatcher : IDispatcher {
    public int SimpleCallCount { get; private set; }
    public int OptionsCallCount { get; private set; }
    public int PublishOnceCallCount { get; private set; }
    public DispatchOptions? LastCapturedOptions { get; private set; }
    public string? LastPublishOnceClaimKey { get; private set; }

    private static DeliveryReceipt _noopReceipt() =>
      DeliveryReceipt.Accepted(new MessageId(Guid.NewGuid()), destination: "test");

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      SimpleCallCount++;
      return Task.FromResult<IDeliveryReceipt>(_noopReceipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
      OptionsCallCount++;
      LastCapturedOptions = options;
      return Task.FromResult<IDeliveryReceipt>(_noopReceipt());
    }

    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) {
      PublishOnceCallCount++;
      LastPublishOnceClaimKey = claimKey;
      return Task.FromResult(true);
    }

    // Everything else — throws if exercised.
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
