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
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 5a of release/v0.647.0-alpha.1 (DLQ forensics follow-up) — locks the
/// invariant that <c>SecurityContextHelper.EstablishFullContextAsync</c> calls
/// inside the worker publish/dispatch paths are wrapped in a per-call timeout.
///
/// <para>Production root cause: a stuck <c>RemoveUserCommand</c>
/// outbox row spun for hundreds of retries with no log, no failure record, no DLQ
/// promotion, no ASB traffic. Root cause: a consumer's
/// <see cref="IMessageSecurityContextProvider"/> implementation hung
/// indefinitely on the envelope's test-pattern tenant id
/// (<c>c0ffee00-cafe-f00d-face-feed12345678</c>). The hang occurred at
/// <c>OutboxDrainWorker._publishBulkAsync:411</c> BEFORE the publish call
/// was even attempted. The cancellation token at the call site is the
/// worker's stoppingToken — only fires on pod shutdown. So the hang persists
/// forever; <c>claim_orphaned_outbox</c> re-leases the row, attempts
/// increment, the cycle repeats with zero forensic signal.</para>
///
/// <para>Locked invariants (per worker that calls
/// <see cref="SecurityContextHelper.EstablishFullContextAsync"/>):</para>
/// <list type="bullet">
/// <item><description>When the call hangs longer than the configured
/// <c>SecurityContextTimeoutSeconds</c>, the worker enqueues a
/// <see cref="MessageFailure"/> with
/// <c>Reason = MessageFailureReason.SecurityContextEstablishmentFailure</c>
/// and a descriptive Error containing "EstablishFullContextAsync timed out"
/// + the timeout value.</description></item>
/// <item><description>When the call succeeds quickly, no failure is
/// enqueued (locks against a spurious-timeout regression).</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class SecurityContextTimeoutTests {

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

  private sealed class _NoOpPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("Slice 5a test should never reach PublishAsync — the hang is in EstablishFullContextAsync upstream.");
  }

  /// <summary>Passthrough deserializer — returns the JsonElement as-is. Lets
  /// _tryResolveTypedEnvelope return a non-null typed envelope so the worker
  /// actually enters the EstablishFullContextAsync branch. Without it, the
  /// security-context call is skipped entirely (typedEnvelope is null).</summary>
  private sealed class _PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] payload, string messageType) => JsonDocument.Parse(payload).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
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

  /// <summary>Simulates a consumer's hung provider — Task.Delay(Timeout.Infinite, ct) so
  /// the call only completes when its CT cancels. Slice 5a's timeout MUST trigger
  /// that cancellation; without the fix, this hangs forever and the test times out.</summary>
  private sealed class _HangingSecurityContextProvider : IMessageSecurityContextProvider {
    public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<IScopeContext?> EstablishContextAsync(IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) {
      Called.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      return null;
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
  /// RED for Slice 5a: drive OutboxDrainWorker.PublishBulkAsync with a hanging
  /// IMessageSecurityContextProvider and a 1-second timeout configured. Asserts
  /// the worker times out and enqueues a SecurityContextEstablishmentFailure
  /// failure within 2 seconds. Pre-fix: the call hangs at line 411 until the
  /// test cancellation kicks in, the failure channel never sees an entry, and
  /// the test times out.
  /// </summary>
  // --- InboxDispatchWorker fakes ---

  private sealed class _FakeInboxChannelWriter : IInboxChannelWriter {
    // Not exercised by this fake: it tracks no in-flight work, so there is nothing to gate on.
    public int InFlightCount => 0;
    public int PruneInFlightOlderThan(TimeSpan age) => 0;
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

  private sealed class _AllStagesReceptorRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private static InboxWork _inboxWork(Guid msgId) =>
    new() {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "Whizbang.Core.Tests.TestFixture.TestMessage, TestAsm",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };

  // --- tests ---

  [Test]
  public async Task InboxDispatchWorker_SecurityContextHangs_TimesOutAndEnqueuesFailureAsync() {
    var hangingProvider = new _HangingSecurityContextProvider();
    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(hangingProvider);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var channel = new _FakeInboxChannelWriter();
    var failure = new _FakeFailureChannel();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      channel,
      new _FakeHandlerCommitChannel(),
      failure,
      gate,
      Options.Create(new InboxDispatchWorkerOptions {
        Enabled = true,
        SecurityContextTimeoutSeconds = 1,
      }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new _PassthroughDeserializer(),
      receptorRegistry: new _AllStagesReceptorRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await channel.WriteAsync(_inboxWork((Guid)TrackedGuid.NewMedo()), cts.Token);

    var captured = await failure.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(captured.Reason).IsEqualTo(MessageFailureReason.SecurityContextEstablishmentFailure)
      .Because("Mirror of OutboxDrainWorker: inbox-side SecurityContext hang must route through the same dedicated reason so dashboards bucket the failure correctly.");
    await Assert.That(captured.Error).Contains("EstablishFullContextAsync timed out")
      .Because("The error text must describe the hang site for operator triage and Slice 2's SQL fingerprint clustering.");
  }

  [Test]
  public async Task OutboxDrainWorker_SecurityContextHangs_TimesOutAndEnqueuesFailureAsync() {
    var failure = new _FakeFailureChannel();
    var hangingProvider = new _HangingSecurityContextProvider();

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(hangingProvider);
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
        SecurityContextTimeoutSeconds = 1,
      }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      new _NoOpPublishStrategy(),
      lifecycleMessageDeserializer: new _PassthroughDeserializer());

    var row = _row((Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo());

    // Drive the internal bulk publish path with our row. The worker should call
    // EstablishFullContextAsync (hanging provider blocks), the timeout should
    // fire at ~1s, and the failure-channel enqueue should land soon after.
    var bulkTask = worker.PublishBulkAsync([row], CancellationToken.None);

    var captured = await failure.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await bulkTask;

    await Assert.That(captured.MessageId).IsEqualTo(row.MessageId)
      .Because("The failing row's id MUST be on the failure record so process_outbox_failures targets the correct wh_outbox row.");
    await Assert.That(captured.Reason).IsEqualTo(MessageFailureReason.SecurityContextEstablishmentFailure)
      .Because("Production-class hangs at EstablishFullContextAsync are operationally distinct from transport faults; the dedicated reason lets dashboards / DLQ recovery policies / operators distinguish them.");
    await Assert.That(captured.Error).Contains("EstablishFullContextAsync timed out")
      .Because("The error text MUST describe the hang site so the wh_outbox.error column (and later wh_dead_letters.error_text + fingerprint) carries useful triage information for operators.");
  }
}
