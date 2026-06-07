using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
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
/// v0.651 inbox forensic-preservation slice — mirror of v0.648's outbox Slice 1.
/// Locks the invariant that <see cref="InboxDispatchWorker"/>'s pre-publish DLQ
/// gate uses the inbox row's <c>wh_inbox.error</c> column (the real exception
/// text from the most recent <c>process_inbox_failures</c> cycle) as
/// <c>errorText</c> when present, falling back to the synthetic meta-message
/// only when the column is NULL.
///
/// <para>Slot-3 Jun 2026 forensic gap: all 38,251 inbox-source DLQ rows on the
/// JDX BFF database carried the synthetic
/// <c>"InboxDispatchWorker dead-lettered: attempts=N > max=10"</c>
/// meta-message, collapsing under a single
/// <c>error_fingerprint</c> (<c>7a66c4fc2d9ddd71</c>) regardless of underlying
/// root cause. v0.648 fixed the symmetric problem on the outbox side via
/// Slice 1's <c>row.Error</c>-preferring logic; v0.651 closes it on the inbox
/// side so operators see distinct clusters per failure mode for new inbox
/// DLQ rows going forward.</para>
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>When <c>work.Error</c> is non-NULL and non-empty AND
/// the pre-publish gate fires (<c>work.Attempts &gt; MaxInboxAttempts</c>),
/// <c>IDeadLetterStore.MoveAsync</c> receives <c>work.Error</c> as
/// <c>errorText</c> verbatim — no truncation, no transformation.</description></item>
/// <item><description>When <c>work.Error</c> is NULL or empty, the gate falls
/// back to the meta-message so the gate-firing signal is preserved even when
/// no prior failure was recorded.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxPrePublishGateForensicPreservationTests {

  // --- fakes ---

  private sealed class _FakeInstanceProvider : IServiceInstanceProvider {
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

  private sealed class _FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
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

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-gen";
  }

  private sealed record _MoveCall(string SourceTable, Guid SourceId, MessageFailureReason FailureReason, string? ErrorText);

  private sealed class _CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<_MoveCall> Moves { get; } = [];
    public TaskCompletionSource<_MoveCall> FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      var call = new _MoveCall(sourceTable, sourceId, failureReason, errorText);
      Moves.Add(call);
      FirstMove.TrySetResult(call);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private static InboxWork _work(int attempts, string? rowError) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
      Error = rowError,
    };
  }

  private static InboxDispatchWorker _worker(_CapturingDeadLetterStore store, IGenerationProvider gen) {
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeInstanceProvider(),
      new _FakeInboxChannelWriter(),
      new _FakeHandlerCommitChannel(),
      new _FakeFailureChannel(),
      gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 10 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      deadLetterStore: store,
      generationProvider: gen);
  }

  // --- tests ---

  /// <summary>
  /// RED for v0.651's inbox-side forensic preservation: drive ProcessOneInnerAsync
  /// with a work whose Attempts exceeds MaxInboxAttempts AND whose Error column
  /// carries a real exception stack. Asserts the pre-publish DLQ gate fires
  /// MoveAsync with the work's actual error as errorText — NOT the synthetic
  /// meta-message "InboxDispatchWorker dead-lettered: attempts=N > max=M".
  /// Pre-fix, the gate always used the meta-message, which collapsed slot-3's
  /// 38k+ inbox-source DLQ rows to a single fingerprint cluster.
  /// </summary>
  [Test]
  public async Task PrePublishGate_WorkHasRealError_PromotesUsingWorkErrorNotMetaMessageAsync() {
    var realStack = """
      System.InvalidOperationException: Receptor 'CreateProductHandler' threw on duplicate product key
         at JDX.JobService.Features.JobFeature.JobDuplicateGuard.AssertUnique(Guid productId)
         at JDX.JobService.Features.JobFeature.Receptors.CreateProductHandler.HandleAsync()
      """;

    var work = _work(attempts: 11, rowError: realStack);
    var dlqStore = new _CapturingDeadLetterStore();
    var worker = _worker(dlqStore, new _FakeGenerationProvider());

    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var move = await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX)
      .Because("DLQ promotion path setup sanity check — this is an inbox-source promotion.");
    await Assert.That(move.SourceId).IsEqualTo(work.MessageId)
      .Because("MoveAsync must identify the work item being promoted; the message_id is the inbox row's primary key.");
    await Assert.That(move.FailureReason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded)
      .Because("The gate fires specifically on MaxInboxAttempts breach — the reason code must reflect that, not the underlying receptor exception class.");
    await Assert.That(move.ErrorText).IsEqualTo(realStack)
      .Because("v0.651 inbox invariant: pre-publish gate MUST use the work's actual error column verbatim — NOT the meta-message. The 38k+ slot-3 DLQ rows all collapsed to one fingerprint because the meta-message was used; using the real stack restores forensic diversity.");
    await Assert.That(move.ErrorText).DoesNotContain("dead-lettered: attempts=")
      .Because("Explicit anti-assertion: the meta-message text must NOT appear in errorText when work.Error is present.");
  }

  /// <summary>
  /// GREEN backstop: when the work has no error captured (NULL), the meta-message
  /// is preserved as the fallback so the DLQ row still records WHY it was
  /// promoted (cap reached) even when no failure was logged against the row.
  /// Mirror of the outbox-side backstop test on
  /// <see cref="PrePublishGateForensicPreservationTests"/>.
  /// </summary>
  [Test]
  public async Task PrePublishGate_WorkErrorIsNull_FallsBackToMetaMessageAsync() {
    var work = _work(attempts: 11, rowError: null);
    var dlqStore = new _CapturingDeadLetterStore();
    var worker = _worker(dlqStore, new _FakeGenerationProvider());

    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var move = await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(move.ErrorText).Contains("dead-lettered")
      .Because("With no work-side error, the meta-message remains the operator's only signal that the gate fired — must be preserved as a fallback.");
    await Assert.That(move.ErrorText).Contains("attempts=11")
      .Because("Meta-message includes the attempt count for operator visibility into how many retries the work item burned.");
    await Assert.That(move.ErrorText).Contains("max=10")
      .Because("Meta-message includes the configured cap so operators can correlate the promotion with the active MaxInboxAttempts setting.");
  }

  /// <summary>
  /// GREEN whitespace edge: when work.Error is non-NULL but whitespace-only (an
  /// edge case some SQL paths can produce by setting error = ''), the gate still
  /// falls back to the meta-message rather than promoting with a blank
  /// errorText that would carry no forensic signal.
  /// </summary>
  [Test]
  public async Task PrePublishGate_WorkErrorIsWhitespace_FallsBackToMetaMessageAsync() {
    var work = _work(attempts: 15, rowError: "   ");
    var dlqStore = new _CapturingDeadLetterStore();
    var worker = _worker(dlqStore, new _FakeGenerationProvider());

    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var move = await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(move.ErrorText).Contains("dead-lettered")
      .Because("string.IsNullOrWhiteSpace must trip on \"   \" the same as null — whitespace carries no forensic signal so the meta-message is the better choice.");
    await Assert.That(move.ErrorText).Contains("attempts=15")
      .Because("Meta-message includes the actual attempts count from this work item, not a hardcoded value.");
  }
}
