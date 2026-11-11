// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Signals;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A publish routed to the outbox batcher tells the batch buffer who it is and whether its scope holds a database
/// transaction open (#2220): such a publish goes past the buffer's limit, up to the ceiling, instead of waiting while it
/// holds a connection the flushes may need. The transaction is the one a data driver's transaction source reports for
/// the scope, or an ambient <see cref="TransactionScope"/>.
/// </summary>
public class StreamAffinityBatchBufferTests {
  private static BatchBuffer _fullAfterOne(FakeLogger<BatchBuffer> logger) => new(
    BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedMessages = 1, MaxBufferedBytes = 0 }, 1_000_000, 0, 0),
    logger, null, new FakeTimeProvider());

  private sealed class Inner : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class Accepting : IOutboxBatchStrategy {
    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class OpenTransaction : DbTransaction {
    public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
  }

  private sealed class TransactionSource(DbTransaction? transaction) : IAppSignalTransactionSource {
    public DbTransaction? CurrentTransaction => transaction;
  }

  private static ServiceProvider _scope(BatchBuffer buffer, DbTransaction? transaction) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IOutboxBatchStrategy>(new BoundedOutboxBatchStrategy(new Accepting(), buffer));
    services.AddSingleton<IAppSignalTransactionSource>(new TransactionSource(null));
    services.AddSingleton<IAppSignalTransactionSource>(new TransactionSource(transaction));
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task APublishInAScopeWithATransactionOpen_GoesPastTheLimit_AndIsNamedAsync() {
    var logger = new FakeLogger<BatchBuffer>();
    var buffer = _fullAfterOne(logger);
    await using var sp = _scope(buffer, new OpenTransaction());
    var strategy = WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new Inner(), sp);

    await strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());
    await strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());

    await Assert.That(buffer.BufferedMessages).IsEqualTo(2);
    await Assert.That(logger.LatestRecord.Message).StartsWith("A publish of Orders.OrderPlaced holds a database transaction open");
  }

  [Test]
  public async Task APublishInAScopeWithNoTransaction_WaitsAtTheLimitAsync() {
    var buffer = _fullAfterOne(new FakeLogger<BatchBuffer>());
    await using var sp = _scope(buffer, null);
    var strategy = WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new Inner(), sp);
    var first = BoundedBatchStrategyTests.Outbox();
    await strategy.QueueOutboxMessageAsync(first);

    var second = strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());

    await Assert.That(second.IsCompleted).IsFalse();
    buffer.Release([first]);
    await second;
  }

  [Test]
  public async Task APublishInsideAnAmbientTransactionScope_GoesPastTheLimitAsync() {
    var buffer = _fullAfterOne(new FakeLogger<BatchBuffer>());
    await using var sp = _scope(buffer, null);
    var strategy = WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new Inner(), sp);
    await strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());

    using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled)) {
      await strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());
    }

    await Assert.That(buffer.BufferedMessages).IsEqualTo(2);
  }

  [Test]
  public async Task AnUnboundedStrategy_IsAppendedToWithoutAContextAsync() {
    var appended = new List<OutboxMessage>();
    var plain = new RecordingAppend(appended);
    var sut = new StreamAffinityWorkCoordinatorStrategy(new Inner(), plain, NullLogger.Instance, auditDecisionHook: null,
      holdsOpenTransaction: () => throw new InvalidOperationException("an unbounded strategy never asks"));
    var message = BoundedBatchStrategyTests.Outbox();

    await sut.QueueOutboxMessageAsync(message);

    await Assert.That(appended).IsEquivalentTo([message]);
  }

  private sealed class RecordingAppend(List<OutboxMessage> appended) : IOutboxBatchStrategy {
    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
      appended.Add(message);
      return ValueTask.CompletedTask;
    }
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
