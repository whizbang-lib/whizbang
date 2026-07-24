using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Detached-stage fire-and-forget internals of
/// <see cref="TransportConsumerWorker.InvokePostLifecycleForEventAsync"/> (fallback path,
/// no lifecycle coordinator):
/// <list type="bullet">
/// <item><description>Scope without an <c>IReceptorInvoker</c> → detached tasks return early
/// and complete successfully (invoker-null guard)</description></item>
/// <item><description>Scoped invoker throwing → the detached task swallows the exception
/// (static context, no logger) and still completes successfully</description></item>
/// </list>
/// Detached tasks are tracked via the <c>trackDetachedTask</c> callback and awaited —
/// deterministic completion, no timers.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerDetachedStageTests {

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackPath_NoScopedInvoker_DetachedTasksCompleteAsync() {
    // Arrange: no coordinator (fallback path) AND no IReceptorInvoker registered in the
    // scope the detached stages spin up — the invoker-null guard must return cleanly.
    var inlineInvoker = new SpyReceptorInvoker();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };
    var services = new ServiceCollection(); // no ILifecycleCoordinator, no IReceptorInvoker
    await using var scopedProvider = services.BuildServiceProvider();

    // Act
    var detachedTasks = new List<Task>();
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, inlineInvoker, lifecycleContext, scopedProvider,
      CancellationToken.None, detachedTasks.Add);
    await Task.WhenAll(detachedTasks);

    // Assert: both detached stages fired their tasks, and both returned early without fault.
    await Assert.That(detachedTasks).Count().IsEqualTo(2)
      .Because("PostAllPerspectivesDetached and PostLifecycleDetached each fire one tracked task.");
    foreach (var task in detachedTasks) {
      await Assert.That(task.IsCompletedSuccessfully).IsTrue()
        .Because("A scope without IReceptorInvoker must make the detached task a clean no-op, never a fault.");
    }

    // The inline invoker still receives the full inline sequence:
    // PostAllPerspectivesInline, ImmediateDetached, PostLifecycleInline, ImmediateDetached.
    var stages = inlineInvoker.InvokedStages;
    await Assert.That(stages).Count().IsEqualTo(4)
      .Because("Detached stages resolve their invoker from the scope (which has none) — only the 4 inline invocations may reach the inline invoker.");
    await Assert.That(stages).Contains(LifecycleStage.PostAllPerspectivesInline);
    await Assert.That(stages).Contains(LifecycleStage.PostLifecycleInline);
    await Assert.That(stages).Contains(LifecycleStage.ImmediateDetached);
  }

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackPath_ScopedInvokerThrows_DetachedTasksSwallowAsync() {
    // Arrange: the scope-resolved invoker (used by the detached stages) throws; the
    // inline invoker is a separate spy. The static detached runner must swallow the
    // exception — it has no logger — and the tracked tasks must complete successfully.
    var inlineInvoker = new SpyReceptorInvoker();
    var throwingInvoker = new ThrowingReceptorInvoker();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };
    var services = new ServiceCollection(); // no ILifecycleCoordinator → fallback
    services.AddSingleton<IReceptorInvoker>(throwingInvoker);
    await using var scopedProvider = services.BuildServiceProvider();

    // Act — must not throw even though every detached invocation faults.
    var detachedTasks = new List<Task>();
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, inlineInvoker, lifecycleContext, scopedProvider,
      CancellationToken.None, detachedTasks.Add);
    await Task.WhenAll(detachedTasks);

    // Assert
    await Assert.That(detachedTasks).Count().IsEqualTo(2);
    foreach (var task in detachedTasks) {
      await Assert.That(task.IsCompletedSuccessfully).IsTrue()
        .Because("Detached-stage exceptions must be swallowed — a faulted fire-and-forget task would surface as an unobserved exception.");
    }
    await Assert.That(throwingInvoker.CallCount).IsEqualTo(2)
      .Because("Each detached stage must reach the scoped invoker once and throw there — proving the catch handler (not the null guard) contained the failure.");

    // Inline flow is unaffected by detached failures.
    var stages = inlineInvoker.InvokedStages;
    await Assert.That(stages).Contains(LifecycleStage.PostAllPerspectivesInline);
    await Assert.That(stages).Contains(LifecycleStage.PostLifecycleInline);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static InboxWork _createInboxWork(Guid eventId) {
    var messageId = new MessageId(eventId);
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new InboxWork {
      MessageId = eventId,
      Envelope = jsonEnvelope,
      MessageType = "Test.TestEvent, Test"
    };
  }

  private static MessageEnvelope<JsonElement> _createTypedEnvelopeWithId(Guid eventId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>Records every stage InvokeAsync is called with (thread-safe).</summary>
  private sealed class SpyReceptorInvoker : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<LifecycleStage> _invokedStages = [];

    public List<LifecycleStage> InvokedStages {
      get {
        lock (_lock) {
          return [.. _invokedStages];
        }
      }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _invokedStages.Add(stage);
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Counts invocations, then throws — exercises the detached-stage catch handler.</summary>
  private sealed class ThrowingReceptorInvoker : IReceptorInvoker {
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _callCount);
      throw new InvalidOperationException("simulated receptor failure in detached stage");
    }
  }
}
