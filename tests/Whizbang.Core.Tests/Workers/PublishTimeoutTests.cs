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
/// Slice 4 of release/v0.648.0-alpha.1 (DLQ forensics follow-up) — locks the
/// invariant that <see cref="IMessagePublishStrategy.PublishBatchAsync"/> and
/// <see cref="IMessagePublishStrategy.PublishAsync"/> calls inside
/// <see cref="OutboxDrainWorker"/> are wrapped in a per-call timeout so a hung
/// transport SDK surfaces as a clean failure instead of blocking the worker
/// until pod shutdown.
///
/// <para>production root cause part 2 (Jun-2026, post-v0.647): the stuck
/// <c>RemoveShellUserCommand</c> outbox row continued to spin even after v0.647
/// (SecurityContext timeout) shipped. Diagnostic: pod logs ZERO publish-related
/// signal across multi-minute windows, ASB topic last-accessed timestamp is
/// 15+ hours stale, the row's <c>error</c> column stays NULL,
/// <c>failure_reason</c> = 99 (default, never set by
/// <c>process_outbox_failures</c>), status bit 32768 (Failed) never sets. The
/// SecurityContext path is past — the hang is in
/// <c>_publishStrategy.PublishBatchAsync</c> after security context succeeds.
/// The Azure SDK call never returns; the worker's CT is the stoppingToken so
/// it only cancels at pod shutdown; the lease eventually expires, claim_orphaned
/// re-leases, attempts increments, repeat forever — same pattern as the
/// SecurityContext hang but one stack frame later.</para>
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>When <see cref="IMessagePublishStrategy.PublishBatchAsync"/>
/// hangs longer than the configured
/// <c>OutboxDrainWorkerOptions.PublishTimeoutSeconds</c>, the worker enqueues a
/// <see cref="MessageFailure"/> per row in the batch with
/// <c>Reason = MessageFailureReason.TransportException</c> and a descriptive
/// Error containing "Publish timed out after Ns — SDK call did not
/// return".</description></item>
/// <item><description>Same invariant for the singular path
/// (<see cref="IMessagePublishStrategy.PublishAsync"/>).</description></item>
/// <item><description>Worker shutdown (parent CT cancellation) still flows OCE
/// up normally — the <c>when</c> filter discriminates timeout-vs-shutdown.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class PublishTimeoutTests {

  // --- fakes ---

  private sealed class _FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class _FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public TaskCompletionSource<MessageFailure> FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      FirstFailure.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
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

  /// <summary>Simulates an Azure SDK call that never returns — the production pattern.
  /// PublishBatchAsync awaits a Task.Delay(Timeout.Infinite, ct); only completes
  /// when the CT cancels. The worker's per-call timeout MUST cancel that CT, or
  /// the test hangs forever and the assertion times out.</summary>
  private sealed class _HangingPublishStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
      return new MessagePublishResult { MessageId = work.MessageId, Success = false, CompletedStatus = work.Status };
    }
    public async Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
      return [];
    }
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  // --- tests ---

  /// <summary>
  /// RED for Slice 4: drive OutboxDrainWorker.PublishBulkAsync with a hanging
  /// transport (PublishBatchAsync never returns) and a 1-second timeout
  /// configured. Asserts the worker times out and enqueues a TransportException
  /// failure per row in the batch within 3 seconds. Pre-fix: the call hangs at
  /// PublishBatchAsync (line 434) until the test cancellation kicks in, the
  /// failure channel never sees an entry, and the test times out — exactly the
  /// production pattern reproduced in unit form.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_PublishBatchHangs_TimesOutAndEnqueuesFailurePerRowAsync() {
    var failure = new _FakeFailureChannel();
    var hangingStrategy = new _HangingPublishStrategy();

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      new _FakeOutboxDrainChannel(),
      new _FakeOutboxCompletionChannel(),
      failure,
      gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        PublishTimeoutSeconds = 1,
      }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      hangingStrategy);

    var row1 = _row((Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo());
    var row2 = _row((Guid)TrackedGuid.NewMedo(), row1.StreamId!.Value);

    // Drive the bulk publish path with two rows. The hanging transport blocks;
    // the per-call timeout fires at ~1s and the failure-channel enqueue should
    // land for each row in the batch.
    var bulkTask = worker.PublishBulkAsync([row1, row2], CancellationToken.None);

    var captured = await failure.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await bulkTask;

    await Assert.That(failure.All.Count).IsGreaterThanOrEqualTo(2)
      .Because("The hanging transport affects every row in the batch. Each row gets its own failure record so process_outbox_failures targets every wh_outbox row in the stuck batch.");
    await Assert.That(captured.Reason).IsEqualTo(MessageFailureReason.TransportException)
      .Because("Transport-layer hangs are operationally distinct from receptor / lifecycle / security-context failures; the dedicated reason lets dashboards / DLQ recovery policies / operators distinguish them.");
    await Assert.That(captured.Error).Contains("Publish timed out after")
      .Because("The error text MUST describe the hang site so the wh_outbox.error column (and later wh_dead_letters.error_text + fingerprint) carries useful triage information for operators.");
    await Assert.That(captured.Error).Contains("SDK call did not return")
      .Because("The diagnostic phrase 'SDK call did not return' explicitly distinguishes this case from a transport that returned with an error — operators reading wh_dead_letters can immediately tell which class of bug this is.");
  }
}
