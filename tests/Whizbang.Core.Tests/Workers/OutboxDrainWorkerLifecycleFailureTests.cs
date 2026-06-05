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

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 1 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks the
/// invariant that <see cref="OutboxDrainWorker.InvokeOutboxLifecycleStageAsync"/>
/// routes lifecycle exceptions through the failure channel so
/// <c>wh_outbox.error</c> is populated.
///
/// <para>
/// production root cause: a stuck <c>RemoveShellUserCommand</c> outbox row ran 295 retry
/// attempts over 24 h with <c>wh_outbox.error = (empty)</c>. The inline-stage
/// <c>receptorInvoker.InvokeAsync</c> threw on every retry, the outer catch only
/// called <c>LogLifecycleStageError</c>, and the failure channel never saw the
/// exception. Operators had no signal until they tailed logs across every pod.
/// </para>
/// <para>
/// Locked invariant: when the inline lifecycle path throws, the failure channel
/// receives exactly one <see cref="MessageFailure"/> for <see cref="WorkCategory.Outbox"/>
/// whose <c>Error</c> contains the full exception text (i.e. <c>ex.ToString()</c>,
/// including stack trace) — so <c>process_outbox_failures</c> can persist the cause
/// into <c>wh_outbox.error</c> and the next DLQ-promotion pass (Slice 3) captures the
/// full stack for fingerprinting.
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class OutboxDrainWorkerLifecycleFailureTests {

  // --- fakes ---

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakePublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      Task.FromResult(new MessagePublishResult { MessageId = work.MessageId, Success = true, CompletedStatus = MessageProcessingStatus.Published });
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
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

  /// <summary>Receptor invoker whose inline call always throws the configured
  /// exception. Slice 1's RED uses this to simulate the production lifecycle-receptor
  /// fault that produced an empty <c>wh_outbox.error</c>.</summary>
  private sealed class _ThrowingReceptorInvoker(Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.FromException(toThrow);
  }

  // --- helpers ---

  private static OutboxDrainWorker _buildWorker(FakeFailureChannel failure) {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(),
      new FakeOutboxDrainChannel(),
      new FakeOutboxCompletionChannel(),
      failure,
      gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      new FakePublishStrategy());
  }

  private static OutboxWork _outboxWork(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    return new OutboxWork {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      Envelope = envelope,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
  }

  // --- tests ---

  /// <summary>
  /// RED for Slice 1: when the inline lifecycle stage throws, the outer catch must
  /// enqueue a <see cref="MessageFailure"/> through <see cref="IFailureChannel"/>
  /// containing the exception's full text. Pre-Slice-1, the catch at
  /// <c>OutboxDrainWorker.InvokeOutboxLifecycleStageAsync:641</c> only logs — so
  /// the assertion <c>HasCount(1)</c> fails (count is 0) and we see exactly the
  /// production symptom: silent failure, empty <c>wh_outbox.error</c>.
  /// </summary>
  [Test]
  public async Task InvokeOutboxLifecycleStage_InlineThrows_EnqueuesFailureWithFullExceptionTextAsync() {
    var failure = new FakeFailureChannel();
    var worker = _buildWorker(failure);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var work = _outboxWork(messageId, streamId);
    var thrown = new InvalidOperationException("simulated lifecycle fault from production regression test");
    var throwingInvoker = new _ThrowingReceptorInvoker(thrown);

    await worker.InvokeOutboxLifecycleStageAsync(
      work,
      work.Envelope,
      throwingInvoker,
      LifecycleStage.PreOutboxDetached,
      LifecycleStage.PreOutboxInline,
      "PreOutbox",
      CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("production: lifecycle exceptions silently swallowed produced 295 retries with empty wh_outbox.error. The failure channel is the only path to populate that column via process_outbox_failures.");
    var (category, captured) = failure.All.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Outbox)
      .Because("Outbox-side fault MUST route through WorkCategory.Outbox so process_outbox_failures targets wh_outbox and not wh_inbox.");
    await Assert.That(captured.MessageId).IsEqualTo(messageId)
      .Because("The failed message's ID must reach the failure record so the SQL update targets the correct outbox row.");
    await Assert.That(captured.Error).Contains("simulated lifecycle fault from production regression test")
      .Because("The exception message must reach wh_outbox.error so operators can triage without tailing logs.");
    await Assert.That(captured.Error).Contains("InvalidOperationException")
      .Because("Full ex.ToString() preserves the exception type — required for Slice 2's fingerprint algorithm which reads the type from the first line.");
    await Assert.That(captured.Error).Contains("PreOutbox")
      .Because("The stage name must appear in the captured error so operators can distinguish Pre- vs Post-outbox lifecycle faults.");
  }
}
