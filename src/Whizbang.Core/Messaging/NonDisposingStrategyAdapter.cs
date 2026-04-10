using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Wraps a singleton <see cref="IWorkCoordinatorStrategy"/> to prevent scope disposal
/// from disposing the underlying singleton. When Interval or Batch strategies are
/// registered as singletons and resolved via a scoped <see cref="IWorkCoordinatorStrategy"/>
/// factory, scope disposal would otherwise destroy the shared singleton instance.
/// This adapter delegates all operations but swallows <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:GeneratorPattern_Interval_ResolvesSingletonAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:GeneratorPattern_Batch_ResolvesSingletonAsync</tests>
public sealed class NonDisposingStrategyAdapter(IWorkCoordinatorStrategy inner) : IWorkCoordinatorStrategy, IWorkFlusher, IAsyncDisposable {
  private readonly IWorkCoordinatorStrategy _inner = inner;

  /// <inheritdoc />
  public void QueueOutboxMessage(OutboxMessage message) => _inner.QueueOutboxMessage(message);
  /// <inheritdoc />
  public void QueueInboxMessage(InboxMessage message) => _inner.QueueInboxMessage(message);
  /// <inheritdoc />
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) => _inner.QueueOutboxCompletion(messageId, completedStatus);
  /// <inheritdoc />
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) => _inner.QueueInboxCompletion(messageId, completedStatus);
  /// <inheritdoc />
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) => _inner.QueueOutboxFailure(messageId, completedStatus, errorMessage);
  /// <inheritdoc />
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) => _inner.QueueInboxFailure(messageId, completedStatus, errorMessage);
  /// <inheritdoc />
  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) => _inner.FlushAsync(flags, mode, ct);

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    _inner.FlushAsync(WorkBatchOptions.SkipInboxClaiming, FlushMode.Required, ct);

  /// <summary>
  /// No-op: the singleton is owned by the DI container, not by scopes.
  /// </summary>
  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
