using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Queues events within a scope and invokes perspectives when scope completes.
/// Implements Unit of Work pattern for perspective materialization.
/// Registered as Scoped service - one instance per HTTP request or message batch.
/// </summary>
/// <docs>core-concepts/perspectives</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
public interface IPerspectiveInvoker : IAsyncDisposable {
  /// <summary>
  /// Queues an event to be sent to perspectives when scope completes.
  /// Called by Event Store after persisting event.
  /// Thread-safe for concurrent queueing within a scope.
  /// </summary>
  /// <tests>No tests found</tests>
  void QueueEvent(IEvent eventData);

  /// <summary>
  /// Invokes perspectives for all queued events.
  /// Automatically called on scope disposal (IAsyncDisposable).
  /// Can be called manually for explicit control.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  Task InvokePerspectivesAsync(CancellationToken cancellationToken = default);
}
