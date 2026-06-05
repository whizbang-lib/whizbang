using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 7 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — audit
/// across all workers' lifecycle catches. Slot-3 root cause was a single silent
/// catch in <see cref="OutboxDrainWorker"/>.<c>InvokeOutboxLifecycleStageAsync</c>
/// — exception logged, no failure-channel enqueue, no wh_outbox.error population,
/// no DLQ promotion. This slice audits the sibling workers to verify the same
/// pattern doesn't exist in inbox/post-outbox paths, AND locks the invariant
/// with regression tests so any future silent-catch reintroduction is caught
/// by the test suite immediately.
///
/// <para>Invariants locked per (worker, stage):</para>
/// <list type="bullet">
/// <item><description><see cref="InboxDispatchWorker"/> PreInboxInline /
/// PostInboxInline: thrown exceptions reach <see cref="IFailureChannel"/> with
/// the full ex.ToString() — Slice 1's fix mirrored from outbox to inbox.</description></item>
/// <item><description><see cref="OutboxDrainWorker"/> PostOutbox path: Slice 1
/// originally tested PreOutbox; confirm the same helper handles PostOutbox
/// symmetrically (it does — the helper is stage-agnostic — but we lock it
/// here so a future refactor that splits Pre/Post doesn't regress half the
/// surface).</description></item>
/// </list>
///
/// <para>Out of scope for Slice 7: <see cref="PerspectiveWorker"/>'s lifecycle
/// uses <c>tracking.AdvanceToAsync</c> with a different catch shape (it records
/// to <c>PostLifecycleErrors</c> metric, not failure channel). That worker
/// surface needs a separate slice — its lifecycle is per-event rather than
/// per-message and the failure-channel semantics don't fit cleanly. Tracked
/// for follow-up; not blocking Slice 8's regression-lock.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class LifecycleExceptionInvariantTests {

  // --- shared fakes ---

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _ThrowingReceptorInvoker(Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.FromException(toThrow);
  }

  private sealed class _FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  // ============================================================
  // OutboxDrainWorker — PostOutbox stage confirmation
  // ============================================================
  // Slice 1 locked PreOutbox via OutboxDrainWorkerLifecycleFailureTests. The
  // helper is stage-agnostic (Pre and Post share one method) so a passing test
  // here proves the helper is symmetric. Lock it explicitly so a future
  // refactor splitting Pre/Post can't regress one half silently.

  private sealed class _FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class _NoOpOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _NoOpOutboxPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      Task.FromResult(new MessagePublishResult { MessageId = work.MessageId, Success = true, CompletedStatus = MessageProcessingStatus.Published });
  }

  [Test]
  public async Task OutboxDrainWorker_PostOutboxLifecycleThrows_EnqueuesFailureAsync() {
    var failure = new _FakeFailureChannel();
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      new _FakeOutboxDrainChannel(),
      new _NoOpOutboxCompletionChannel(),
      failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      new _NoOpOutboxPublishStrategy());

    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      Envelope = envelope,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
    var thrown = new InvalidOperationException("simulated PostOutbox lifecycle fault");

    await worker.InvokeOutboxLifecycleStageAsync(
      work, envelope, new _ThrowingReceptorInvoker(thrown),
      LifecycleStage.PostOutboxDetached, LifecycleStage.PostOutboxInline,
      "PostOutbox", CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Slice 1's helper is stage-agnostic — Post stages MUST share the failure-channel enqueue with Pre. If this fails, a future refactor split Pre/Post without preserving the surface and operators lose triage signal for post-publish lifecycle faults.");
    var (category, captured) = failure.All.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Outbox);
    await Assert.That(captured.Error).Contains("PostOutbox")
      .Because("Stage name must surface in error_text so operators can distinguish Pre vs Post lifecycle failures at the wh_outbox.error level.");
    await Assert.That(captured.Error).Contains("simulated PostOutbox lifecycle fault")
      .Because("Exception message MUST reach the failure record — Slice 2's fingerprint reads from this text.");
  }

  // ============================================================
  // InboxDispatchWorker — Pre/Post Inbox stages (slot-3 audit hole)
  // ============================================================
  // Pre-Slice-7 audit, InboxDispatchWorker.cs:497 had the SAME silent-swallow
  // shape as slot-3's OutboxDrainWorker.cs bug — catch (Exception ex) {
  // LogLifecycleError(...); } with no failure-channel enqueue. Inbox-side
  // lifecycle exceptions retried forever silently, wh_inbox.error stayed empty.
  // Slice 7's GREEN mirrors Slice 1's fix to the inbox path.

  private sealed class _FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly System.Threading.Channels.Channel<InboxWork> _channel = System.Threading.Channels.Channel.CreateUnbounded<InboxWork>();
    public System.Threading.Channels.ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class _FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  [Test]
  public async Task InboxDispatchWorker_PreInboxLifecycleThrows_EnqueuesFailureAsync() {
    var failure = new _FakeFailureChannel();
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      new _FakeInboxChannelWriter(),
      new _FakeHandlerCommitChannel(),
      failure,
      gate,
      Options.Create(new InboxDispatchWorkerOptions { Enabled = true }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "TestMessage",
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
    var thrown = new InvalidOperationException("simulated PreInbox lifecycle fault");
    await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

    await worker.InvokeInboxLifecycleStageAsync(
      work, envelope, scope,
      new _ThrowingReceptorInvoker(thrown),
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Slice 7 fix: inbox lifecycle exceptions had the same slot-3-class silent-swallow pattern as outbox. The fix mirrors Slice 1 to the inbox path so wh_inbox.error captures the cause.");
    var (category, captured) = failure.All.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Inbox)
      .Because("Inbox-side fault must route through WorkCategory.Inbox so process_inbox_failures targets wh_inbox not wh_outbox.");
    await Assert.That(captured.MessageId).IsEqualTo(messageId);
    await Assert.That(captured.Error).Contains("simulated PreInbox lifecycle fault")
      .Because("Exception message must reach wh_inbox.error for operator triage.");
    await Assert.That(captured.Error).Contains("PreInbox")
      .Because("Stage name distinguishes Pre vs Post inbox lifecycle faults in wh_inbox.error.");
  }

  [Test]
  public async Task InboxDispatchWorker_PostInboxLifecycleThrows_EnqueuesFailureAsync() {
    var failure = new _FakeFailureChannel();
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      new _FakeInboxChannelWriter(),
      new _FakeHandlerCommitChannel(),
      failure,
      gate,
      Options.Create(new InboxDispatchWorkerOptions { Enabled = true }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "TestMessage",
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
    var thrown = new InvalidOperationException("simulated PostInbox lifecycle fault");
    await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

    await worker.InvokeInboxLifecycleStageAsync(
      work, envelope, scope,
      new _ThrowingReceptorInvoker(thrown),
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
      "PostInbox", CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Post-inbox lifecycle faults MUST also surface through the failure channel — symmetric with Pre-inbox so any future refactor splitting them can't regress one half silently.");
    await Assert.That(failure.All.Single().Failure.Error).Contains("PostInbox");
  }
}
