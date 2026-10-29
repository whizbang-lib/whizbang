// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1883: the per-stream batchers start a stream's drain worker from inside the first append for that stream. Started
/// with <see cref="Task.Run(Func{Task}, CancellationToken)"/>, the worker took a copy of that caller's
/// <see cref="ExecutionContext"/>, so every later flush of the stream ran with the first appender's
/// <see cref="AsyncLocal{T}"/> values: its pinned connection (<see cref="PinnedConnectionContext.Current"/>), long
/// after the caller released it or while the caller was still using it, and its scope, tenant and activity, carried
/// into flushes of other callers' messages. A drain worker belongs to the batcher, not to whoever appended first, so
/// it starts with no ambient context at all.
/// </summary>
/// <remarks>
/// Each test makes the first append inside a pinned connection, a value of a test-owned <see cref="AsyncLocal{T}"/>
/// and a started <see cref="Activity"/>, leaves all three, and then stops the batcher, which flushes the buffered
/// message on the stream's worker. The flush records what it can see and signals; none of the three may be visible.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowInboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowApplyBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerStreamSerializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/AmbientFreeTask.cs</code-under-test>
public class BatcherAmbientContextTests {
  private const string FIRST_APPENDER = "first-appender";

  private static readonly AsyncLocal<string?> _callerScope = new();

  /// <summary>What a flush could see of the context it ran in.</summary>
  private sealed record Seen(DbConnection? PinnedConnection, string? CallerScope, Activity? Activity) {
    public static Seen Now() => new(PinnedConnectionContext.Current, _callerScope.Value, Activity.Current);
  }

  [Test]
  [Timeout(30000)]
  public async Task OutboxFlush_DoesNotSeeTheFirstAppendersAmbientContextAsync(CancellationToken cancellationToken) {
    var seen = new TaskCompletionSource<Seen>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (_, _) => {
        seen.TrySetResult(Seen.Now());
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance);

    await _asTheFirstAppenderAsync(() => sut.AppendAsync(_outbox(TrackedGuid.New()), cancellationToken));
    await sut.FlushAndStopAsync(cancellationToken);

    await _assertNothingLeakedAsync(await seen.Task.WaitAsync(cancellationToken));
  }

  [Test]
  [Timeout(30000)]
  public async Task InboxFlush_DoesNotSeeTheFirstAppendersAmbientContextAsync(CancellationToken cancellationToken) {
    var seen = new TaskCompletionSource<Seen>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => {
        seen.TrySetResult(Seen.Now());
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance);

    await _asTheFirstAppenderAsync(() => sut.AppendAsync(_inbox(TrackedGuid.New()), cancellationToken));
    await sut.FlushAndStopAsync(cancellationToken);

    await _assertNothingLeakedAsync(await seen.Task.WaitAsync(cancellationToken));
  }

  [Test]
  [Timeout(30000)]
  public async Task ApplyFlush_DoesNotSeeTheFirstAppendersAmbientContextAsync(CancellationToken cancellationToken) {
    var seen = new TaskCompletionSource<Seen>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => {
        seen.TrySetResult(Seen.Now());
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance);

    await _asTheFirstAppenderAsync(() => sut.AppendAsync(TrackedGuid.New(), cancellationToken));
    await sut.FlushAndStopAsync(cancellationToken);

    await _assertNothingLeakedAsync(await seen.Task.WaitAsync(cancellationToken));
  }

  [Test]
  [Timeout(30000)]
  public async Task PerStreamSerializerProcessor_DoesNotSeeTheFirstEnqueuersAmbientContextAsync(
      CancellationToken cancellationToken) {
    var seen = new TaskCompletionSource<Seen>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var sut = new PerStreamSerializer<Guid>(
      streamIdSelector: id => id,
      processor: (_, _) => {
        seen.TrySetResult(Seen.Now());
        return Task.CompletedTask;
      },
      logger: NullLogger.Instance);

    await _asTheFirstAppenderAsync(() => sut.EnqueueAsync(TrackedGuid.New(), cancellationToken));

    await _assertNothingLeakedAsync(await seen.Task.WaitAsync(cancellationToken));
  }

  [Test]
  public async Task AmbientFreeTask_StartedWhileFlowIsAlreadySuppressed_RunsTheWorkAsync(
      CancellationToken cancellationToken) {
    // A caller that already suppressed the flow (a host starting its own detached work) must still be able to start a
    // worker: suppressing it a second time throws.
    var ran = new TaskCompletionSource<Seen>(TaskCreationOptions.RunContinuationsAsynchronously);
    Task started;
    bool stillSuppressed;
    _callerScope.Value = FIRST_APPENDER;
    // Nothing is awaited while the flow is suppressed: the undo has to run on the thread that suppressed it.
    var flow = ExecutionContext.SuppressFlow();
    try {
      started = AmbientFreeTask.Run(() => {
        ran.TrySetResult(Seen.Now());
        return Task.CompletedTask;
      }, CancellationToken.None);
      stillSuppressed = ExecutionContext.IsFlowSuppressed();
    } finally {
      flow.Undo();
      _callerScope.Value = null;
    }

    await Assert.That(stillSuppressed).IsTrue()
      .Because("the flow the caller suppressed stays suppressed after the start");
    await started.WaitAsync(cancellationToken);
    await Assert.That((await ran.Task.WaitAsync(cancellationToken)).CallerScope).IsNull();
  }

  [Test]
  public async Task AmbientFreeTask_RestoresTheCallersFlowAfterTheStartAsync() {
    var started = AmbientFreeTask.Run(() => Task.CompletedTask, CancellationToken.None);

    await Assert.That(ExecutionContext.IsFlowSuppressed()).IsFalse()
      .Because("the start suppresses the flow only around itself, so the caller's later awaits still flow");
    await started;
  }

  /// <summary>
  /// Runs <paramref name="append"/> as a caller holding a pinned connection, a scope value and an activity, and leaves
  /// all three before returning, as <see cref="InboxDispatchWorker"/> does around the handler that appends.
  /// </summary>
  private static async Task _asTheFirstAppenderAsync(Func<ValueTask> append) {
    await using var pinned = new StubDbConnection();
    using var activity = new Activity("first-appender").Start();
    using (PinnedConnectionContext.Push(pinned)) {
      _callerScope.Value = FIRST_APPENDER;
      try {
        await append();
      } finally {
        _callerScope.Value = null;
      }
    }
  }

  private static async Task _assertNothingLeakedAsync(Seen seen) {
    await Assert.That(seen.PinnedConnection).IsNull()
      .Because("the first appender's pinned connection belongs to that caller, which has released it (#1883)");
    await Assert.That(seen.CallerScope).IsNull()
      .Because("a stream's flush must not carry the first appender's scope into other callers' messages (#1883)");
    await Assert.That(seen.Activity).IsNull()
      .Because("a stream's flush must not be traced as part of the first appender's activity (#1883)");
  }

  private static OutboxMessage _outbox(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    };
  }

  private static InboxMessage _inbox(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
    };
  }

  /// <summary>A connection that is only ever compared by reference; nothing opens it.</summary>
  private sealed class StubDbConnection : DbConnection {
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "";
    public override ConnectionState State => ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() { }
    public override void Open() => throw new NotSupportedException();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
  }
}
