using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using LogEventId = Microsoft.Extensions.Logging.EventId;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="ImmediateAsyncDrainer"/> - the helper that drains ImmediateAsync
/// lifecycle events after a lifecycle stage completes.
/// </summary>
/// <docs>core-concepts/lifecycle-stages#immediate-async</docs>
public class ImmediateAsyncDrainerTests {

  private sealed record TestMessage(string Value) : IMessage;
  private sealed record TestEvent(Guid Id) : IEvent;
  private sealed record AnotherEvent(Guid Id) : IEvent;

  // ==========================================================================
  // Construction tests
  // ==========================================================================

  [Test]
  public async Task Constructor_DefaultThreshold_IsTenAsync() {
    // Arrange & Act
    var drainer = new ImmediateAsyncDrainer();

    // Assert - just verify it creates without error and queue is empty
    await Assert.That(drainer.PendingCount).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_CustomThreshold_AcceptsValueAsync() {
    // Arrange & Act
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 5);

    // Assert
    await Assert.That(drainer.PendingCount).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_ZeroThreshold_DefaultsToTenAsync() {
    // Arrange & Act - zero threshold should default to 10
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 0);

    // Assert - no exception
    await Assert.That(drainer.PendingCount).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_NegativeThreshold_DefaultsToTenAsync() {
    // Arrange & Act - negative threshold should default to 10
    var drainer = new ImmediateAsyncDrainer(warningThreshold: -5);

    // Assert - no exception
    await Assert.That(drainer.PendingCount).IsEqualTo(0);
  }

  // ==========================================================================
  // Enqueue tests
  // ==========================================================================

  [Test]
  public async Task Enqueue_SingleEnvelope_IncrementsPendingCountAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var envelope = _createEnvelope(new TestMessage("test"));

    // Act
    drainer.Enqueue(envelope);

    // Assert
    await Assert.That(drainer.PendingCount).IsEqualTo(1);
  }

  [Test]
  public async Task Enqueue_MultipleEnvelopes_IncrementsPendingCountAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();

    // Act
    drainer.Enqueue(_createEnvelope(new TestMessage("a")));
    drainer.Enqueue(_createEnvelope(new TestMessage("b")));
    drainer.Enqueue(_createEnvelope(new TestMessage("c")));

    // Assert
    await Assert.That(drainer.PendingCount).IsEqualTo(3);
  }

  [Test]
  public async Task Enqueue_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(
      () => { drainer.Enqueue(null!); return Task.CompletedTask; });
  }

  [Test]
  public async Task Enqueue_WithContext_AcceptsContextAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var envelope = _createEnvelope(new TestMessage("test"));
    var context = new LifecycleExecutionContext { CurrentStage = LifecycleStage.ImmediateAsync };

    // Act
    drainer.Enqueue(envelope, context);

    // Assert
    await Assert.That(drainer.PendingCount).IsEqualTo(1);
  }

  // ==========================================================================
  // DrainAsync tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_EmptyQueue_ReturnsZeroAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(invoker.Invocations).IsEmpty();
  }

  [Test]
  public async Task DrainAsync_SingleItem_InvokesReceptorAndReturnsOneAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();
    var envelope = _createEnvelope(new TestMessage("test"));
    drainer.Enqueue(envelope);

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(invoker.Invocations).Count().IsEqualTo(1);
    await Assert.That(invoker.Invocations[0].Stage).IsEqualTo(LifecycleStage.ImmediateAsync);
  }

  [Test]
  public async Task DrainAsync_MultipleItems_InvokesAllInFIFOOrderAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();

    var envelope1 = _createEnvelope(new TestMessage("first"));
    var envelope2 = _createEnvelope(new TestMessage("second"));
    var envelope3 = _createEnvelope(new TestMessage("third"));

    drainer.Enqueue(envelope1);
    drainer.Enqueue(envelope2);
    drainer.Enqueue(envelope3);

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drained).IsEqualTo(3);
    await Assert.That(invoker.Invocations).Count().IsEqualTo(3);

    // Verify FIFO order - first enqueued should be first invoked
    await Assert.That(invoker.Invocations[0].Envelope).IsEqualTo(envelope1);
    await Assert.That(invoker.Invocations[1].Envelope).IsEqualTo(envelope2);
    await Assert.That(invoker.Invocations[2].Envelope).IsEqualTo(envelope3);
  }

  [Test]
  public async Task DrainAsync_PassesContextToInvokerAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();
    var envelope = _createEnvelope(new TestMessage("test"));
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.ImmediateAsync,
      StreamId = Guid.CreateVersion7()
    };
    drainer.Enqueue(envelope, context);

    // Act
    await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(invoker.Invocations[0].Context).IsEqualTo(context);
  }

  [Test]
  public async Task DrainAsync_ClearsQueueAfterDrainingAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();
    drainer.Enqueue(_createEnvelope(new TestMessage("test")));

    // Act
    await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drainer.PendingCount).IsEqualTo(0);
  }

  [Test]
  public async Task DrainAsync_NullInvoker_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(
      async () => await drainer.DrainAsync(null!));
  }

  // ==========================================================================
  // Chaining tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_ChainingEnqueuesDuringDrain_ProcessesNewItemsAsync() {
    // Arrange - Create a drainer and an invoker that enqueues new items during drain
    var drainer = new ImmediateAsyncDrainer();
    var chainEnvelope = _createEnvelope(new TestEvent(Guid.CreateVersion7()));

    // The invoker will enqueue a new item when processing the first item
    var enqueueOnce = true;
    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      if (enqueueOnce) {
        enqueueOnce = false;
        drainer.Enqueue(chainEnvelope);
      }
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("first")));

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert - Both original and chained items should be processed
    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(invoker.InvocationCount).IsEqualTo(2);
  }

  [Test]
  public async Task DrainAsync_DeepChaining_ProcessesAllLevelsAsync() {
    // Arrange - Create a chain of depth 5
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 100);
    var chainDepth = 0;
    const int maxDepth = 5;

    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      if (++chainDepth < maxDepth) {
        drainer.Enqueue(_createEnvelope(new TestEvent(Guid.CreateVersion7())));
      }
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("root")));

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drained).IsEqualTo(maxDepth);
    await Assert.That(invoker.InvocationCount).IsEqualTo(maxDepth);
  }

  // ==========================================================================
  // Warning threshold tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_ExceedsThreshold_LogsWarningAsync() {
    // Arrange
    var logMessages = new ConcurrentBag<string>();
    var logger = new TestLogger(logMessages);
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 3, logger: logger);
    var invoker = new TrackingReceptorInvoker();

    // Enqueue 6 items (exceeds threshold of 3 twice: at 3 and 6)
    for (var i = 0; i < 6; i++) {
      drainer.Enqueue(_createEnvelope(new TestMessage($"item-{i}")));
    }

    // Act
    await drainer.DrainAsync(invoker);

    // Assert - Should have logged warnings at depth 3 and 6
    await Assert.That(logMessages.Count).IsEqualTo(2);
  }

  [Test]
  public async Task DrainAsync_BelowThreshold_NoWarningAsync() {
    // Arrange
    var logMessages = new ConcurrentBag<string>();
    var logger = new TestLogger(logMessages);
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 10, logger: logger);
    var invoker = new TrackingReceptorInvoker();

    // Enqueue 5 items (below threshold of 10)
    for (var i = 0; i < 5; i++) {
      drainer.Enqueue(_createEnvelope(new TestMessage($"item-{i}")));
    }

    // Act
    await drainer.DrainAsync(invoker);

    // Assert - No warnings
    await Assert.That(logMessages).IsEmpty();
  }

  [Test]
  public async Task DrainAsync_ExactlyAtThreshold_LogsOneWarningAsync() {
    // Arrange
    var logMessages = new ConcurrentBag<string>();
    var logger = new TestLogger(logMessages);
    var drainer = new ImmediateAsyncDrainer(warningThreshold: 5, logger: logger);
    var invoker = new TrackingReceptorInvoker();

    // Enqueue exactly 5 items (threshold is 5, warning at depth 5)
    for (var i = 0; i < 5; i++) {
      drainer.Enqueue(_createEnvelope(new TestMessage($"item-{i}")));
    }

    // Act
    await drainer.DrainAsync(invoker);

    // Assert - Exactly one warning at depth 5
    await Assert.That(logMessages.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // Cancellation tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();
    drainer.Enqueue(_createEnvelope(new TestMessage("test")));

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(
      async () => await drainer.DrainAsync(invoker, cts.Token));
  }

  [Test]
  public async Task DrainAsync_CancelledDuringDrain_StopsProcessingAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    using var cts = new CancellationTokenSource();

    // Cancel after first invocation
    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      cts.Cancel();
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("first")));
    drainer.Enqueue(_createEnvelope(new TestMessage("second")));

    // Act & Assert - Should throw after processing first item
    await Assert.ThrowsAsync<OperationCanceledException>(
      async () => await drainer.DrainAsync(invoker, cts.Token));

    // First item was processed, second was not
    await Assert.That(invoker.InvocationCount).IsEqualTo(1);
  }

  // ==========================================================================
  // Concurrent enqueue during drain tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_ConcurrentEnqueueDuringDrain_ProcessesNewItemsAsync() {
    // Arrange - Simulate concurrent enqueue from another thread during drain
    var drainer = new ImmediateAsyncDrainer();
    var processedCount = 0;

    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      Interlocked.Increment(ref processedCount);

      // On first invocation, enqueue from current thread (simulates concurrent enqueue)
      if (processedCount == 1) {
        drainer.Enqueue(_createEnvelope(new TestEvent(Guid.CreateVersion7())));
        drainer.Enqueue(_createEnvelope(new AnotherEvent(Guid.CreateVersion7())));
      }
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("initial")));

    // Act
    var drained = await drainer.DrainAsync(invoker);

    // Assert - Original + 2 concurrent items
    await Assert.That(drained).IsEqualTo(3);
  }

  // ==========================================================================
  // Reuse tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_CalledTwice_SecondCallProcessesNewItemsOnlyAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new TrackingReceptorInvoker();

    drainer.Enqueue(_createEnvelope(new TestMessage("batch1")));

    // Act - First drain
    var drained1 = await drainer.DrainAsync(invoker);

    // Enqueue more
    drainer.Enqueue(_createEnvelope(new TestMessage("batch2")));

    // Act - Second drain
    var drained2 = await drainer.DrainAsync(invoker);

    // Assert
    await Assert.That(drained1).IsEqualTo(1);
    await Assert.That(drained2).IsEqualTo(1);
    await Assert.That(invoker.Invocations).Count().IsEqualTo(2);
  }

  // ==========================================================================
  // Error propagation tests
  // ==========================================================================

  [Test]
  public async Task DrainAsync_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      throw new InvalidOperationException("Receptor failed");
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("test")));

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await drainer.DrainAsync(invoker));
  }

  [Test]
  public async Task DrainAsync_InvokerThrowsOnSecondItem_FirstItemProcessedAsync() {
    // Arrange
    var drainer = new ImmediateAsyncDrainer();
    var invocationCount = 0;
    var invoker = new CallbackReceptorInvoker((envelope, stage, context, ct) => {
      if (++invocationCount == 2) {
        throw new InvalidOperationException("Second item failed");
      }
    });

    drainer.Enqueue(_createEnvelope(new TestMessage("first")));
    drainer.Enqueue(_createEnvelope(new TestMessage("second")));

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await drainer.DrainAsync(invoker));

    // First item was processed
    await Assert.That(invocationCount).IsEqualTo(2);
  }

  // ==========================================================================
  // Test helpers
  // ==========================================================================

  private static MessageEnvelope<T> _createEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  /// <summary>
  /// IReceptorInvoker that tracks invocations for assertions.
  /// </summary>
  private sealed class TrackingReceptorInvoker : IReceptorInvoker {
    public List<(IMessageEnvelope Envelope, LifecycleStage Stage, ILifecycleContext? Context)> Invocations { get; } = [];

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Invocations.Add((envelope, stage, context));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// IReceptorInvoker that executes a callback on each invocation.
  /// </summary>
  private sealed class CallbackReceptorInvoker(
      Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?, CancellationToken> callback) : IReceptorInvoker {
    private readonly Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?, CancellationToken> _callback = callback;
    private int _invocationCount;

    public int InvocationCount => _invocationCount;

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _invocationCount);
      _callback(envelope, stage, context, cancellationToken);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Simple logger that records warning messages for assertion.
  /// </summary>
  private sealed class TestLogger(ConcurrentBag<string> messages) : ILogger {
    private readonly ConcurrentBag<string> _messages = messages;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, LogEventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Warning) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
