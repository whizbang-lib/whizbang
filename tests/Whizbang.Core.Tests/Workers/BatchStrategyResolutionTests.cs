// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A batch strategy a consumer registers directly, outside the pipeline's registration, is bounded too (#2220): the
/// framework wraps it where it resolves the strategy it appends to, once per instance.
/// </summary>
public class BatchStrategyResolutionTests {
  private sealed class RawOutbox : IOutboxBatchStrategy {
    public int Appended;
    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
      Appended++;
      return ValueTask.CompletedTask;
    }
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class RawInbox : IInboxBatchStrategy {
    public ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class NoOpInner : IWorkCoordinatorStrategy {
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

  private static ServiceProvider _withRawStrategies(RawOutbox outbox, RawInbox inbox) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:BatchBuffer:MaxBufferedMessages"] = "1" }).Build());
    services.AddWhizbangWorkers();
    // Registered directly, after the pipeline: the last registration wins, and nothing in it is bounded.
    services.AddSingleton<IOutboxBatchStrategy>(outbox);
    services.AddSingleton<IInboxBatchStrategy>(inbox);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task ARawOutboxRegistration_IsAppendedToBounded_AndBlocksAtTheLimitAsync() {
    var raw = new RawOutbox();
    await using var sp = _withRawStrategies(raw, new RawInbox());
    var strategy = WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new NoOpInner(), sp);

    await strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());
    var second = strategy.QueueOutboxMessageAsync(BoundedBatchStrategyTests.Outbox());

    await Assert.That(raw.Appended).IsEqualTo(1);
    await Assert.That(second.IsCompleted).IsFalse().Because("the raw strategy never flushes, so the buffer stays full");
    var bounded = (BoundedOutboxBatchStrategy)BatchStrategyResolution.Outbox(sp)!;
    await Assert.That(bounded.Inner).IsSameReferenceAs(raw);
    await bounded.FlushAndStopAsync();
    await Assert.That(async () => await second).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task ARawRegistration_IsWrappedOnce_AndABoundedOneIsReturnedAsItIsAsync() {
    var rawInbox = new RawInbox();
    await using var raw = _withRawStrategies(new RawOutbox(), rawInbox);
    var defaults = new ServiceCollection().AddLogging().AddWhizbangWorkers();
    await using var pipeline = defaults.BuildServiceProvider();

    await Assert.That(BatchStrategyResolution.Outbox(raw)).IsSameReferenceAs(BatchStrategyResolution.Outbox(raw));
    await Assert.That(BatchStrategyResolution.Inbox(raw)).IsSameReferenceAs(BatchStrategyResolution.Inbox(raw));
    await Assert.That(((BoundedInboxBatchStrategy)BatchStrategyResolution.Inbox(raw)!).Inner).IsSameReferenceAs(rawInbox);
    await Assert.That(BatchStrategyResolution.Outbox(pipeline)).IsSameReferenceAs(pipeline.GetRequiredService<IOutboxBatchStrategy>());
    await Assert.That(BatchStrategyResolution.Inbox(pipeline)).IsSameReferenceAs(pipeline.GetRequiredService<IInboxBatchStrategy>());
  }

  [Test]
  public async Task WithoutTheWorkerPipeline_ARawRegistrationIsReturnedAsItIs_AndNoneIsNullAsync() {
    var outbox = new RawOutbox();
    var inbox = new RawInbox();
    await using var bare = new ServiceCollection()
      .AddSingleton<IOutboxBatchStrategy>(outbox).AddSingleton<IInboxBatchStrategy>(inbox).BuildServiceProvider();
    await using var empty = new ServiceCollection().BuildServiceProvider();

    await Assert.That(BatchStrategyResolution.Outbox(bare)).IsSameReferenceAs(outbox);
    await Assert.That(BatchStrategyResolution.Inbox(bare)).IsSameReferenceAs(inbox);
    await Assert.That(BatchStrategyResolution.Outbox(empty)).IsNull();
    await Assert.That(BatchStrategyResolution.Inbox(empty)).IsNull();
  }
}
