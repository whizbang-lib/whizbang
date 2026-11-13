// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage tests for the DI-factory registrations in <see cref="WorkerPipelineExtensions"/> that
/// only run when something actually RESOLVES the registered service. <c>TryAddSingleton</c> with a
/// factory lambda never executes that lambda at registration time — a broken dependency chain (a
/// missing binding, a constructor signature drift) stays invisible until the first on-demand
/// resolution in production. Every test here builds the provider and resolves — and where cheap,
/// invokes — the target service instead of only counting descriptors.
/// </summary>
[Category("Workers")]
public class WorkerPipelineExtensionsCoverageTests {

  [Test]
  public async Task IntegritySweepRunner_ResolvesToTheSameSingletonAsIntegrityAuditWorkerAsync() {
    // #80-D: the audit worker doubles as the sweep runner (the scheduled occurrence's receptor
    // resolves IIntegritySweepRunner expecting the SAME instance whose ExecuteAsync loop and
    // cycle counter it is reporting on). A factory that resolved a second, independent instance
    // would silently orphan sweep-state tracking — the same class of bug this file's own history
    // records for the HousekeepingCoordinator decisions metric that never reached telemetry.
    var services = _composeWorkerPipeline();
    await using var provider = services.BuildServiceProvider();

    var worker = provider.GetRequiredService<IntegrityAuditWorker>();
    var sweepRunner = provider.GetRequiredService<IIntegritySweepRunner>();

    await Assert.That(ReferenceEquals(sweepRunner, worker)).IsTrue()
      .Because("the sweep runner interface must resolve to the exact IntegrityAuditWorker instance — a second instance would silently orphan sweep-state tracking");
  }

  [Test]
  public async Task StreamPrefixTruncatorAndStreamCompactor_ResolveAsSingletonsAndCompactorRunsEndToEndAsync() {
    // Both IStreamPrefixTruncator and IStreamCompactor are TryAddSingleton factories chaining several
    // dependencies (IWorkCoordinator, IEventStore, IPerspectiveSnapshotStore, and — for the
    // compactor, IStreamPrefixTruncator itself). Registering the factory proves nothing about whether
    // the chain actually builds; on-demand callers (E3 tier-2 compaction, an admin operation)
    // are the first ones who would discover a broken binding otherwise.
    var services = _composeWorkerPipeline();
    services.AddSingleton<IWorkCoordinator>(new NoOpWorkCoordinator());
    services.AddSingleton<IEventStore>(new InMemoryEventStore());
    services.AddSingleton<IPerspectiveSnapshotStore>(new NoSnapshotStore());

    await using var provider = services.BuildServiceProvider();

    var closerFirst = provider.GetRequiredService<IStreamPrefixTruncator>();
    var closerSecond = provider.GetRequiredService<IStreamPrefixTruncator>();
    var compactorFirst = provider.GetRequiredService<IStreamCompactor>();
    var compactorSecond = provider.GetRequiredService<IStreamCompactor>();

    await Assert.That(closerFirst).IsTypeOf<StreamPrefixTruncator>()
      .Because("the registration promises a real StreamPrefixTruncator wired to the container's dependencies, not a stand-in");
    await Assert.That(ReferenceEquals(closerFirst, closerSecond)).IsTrue()
      .Because("TryAddSingleton claims one instance for the process; a second instance racing the A1 close-truncate path would be a correctness bug");
    await Assert.That(compactorFirst).IsTypeOf<StreamCompactor>()
      .Because("the registration promises a real StreamCompactor wired to the container's dependencies, not a stand-in");
    await Assert.That(ReferenceEquals(compactorFirst, compactorSecond)).IsTrue()
      .Because("a second, independently-constructed compactor sharing no state with the first defeats the singleton wiring the registration claims");

    // Exercises the resolved compactor's real dependency chain end-to-end (no snapshot yet is a
    // legitimate, DB-free branch) rather than stopping at "it resolved without throwing".
    var result = await compactorFirst.CompactAsync(Guid.NewGuid(), "SomePerspective", CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo("no_snapshot")
      .Because("the resolved compactor must actually run its real dependency chain, not just type-check — the no-snapshot branch is reachable without a live database");
  }

  [Test]
  public async Task OutboxCompletionChannel_ResolvesToTheSameSingletonAsOutboxCompletionFlushWorkerAsync() {
    // IOutboxCompletionChannel exists so OutboxPublishWorker can enqueue completed-publish ids
    // without depending on the concrete flush worker type. If the factory ever resolved a
    // DIFFERENT instance than the one whose ExecuteAsync loop actually drains and flushes,
    // every id written through the channel would vanish into a sink nobody reads.
    var services = _composeWorkerPipeline();
    await using var provider = services.BuildServiceProvider();

    var worker = provider.GetRequiredService<OutboxCompletionFlushWorker>();
    var channel = provider.GetRequiredService<IOutboxCompletionChannel>();

    await Assert.That(ReferenceEquals(channel, worker)).IsTrue()
      .Because("producers write through IOutboxCompletionChannel; a second instance would mean enqueued ids never reach the worker that flushes them");
  }

  [Test]
  public async Task OutboxBulkFlushCallback_WhenLifecycleStagesThrow_StillStoresAndLogsBothFailuresAsync() {
    // The call site's own comment is explicit: "The storage call MUST happen; without it the
    // message is permanently lost." A misbehaving deserializer or receptor at either inline stage
    // (before store and after it) must never block -- or silently skip logging -- the actual
    // outbox storage write. Each failure is logged against its own stage and message (#2310).
    var messages = new[] { _buildOutboxMessage() };
    var messageId = messages[0].MessageId.ToString();
    var coordinator = new NoOpWorkCoordinator();
    var loggerProvider = new RecordingLoggerProvider();

    var services = _composeWorkerPipeline();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ILifecycleMessageDeserializer>(new ThrowingLifecycleMessageDeserializer());
    services.AddSingleton<ILoggerFactory>(new LoggerFactory([loggerProvider]));
    // The callback's first statement awaits the schema gate. Without an already-ready one it parks
    // forever and the test hangs to its timeout rather than failing -- the storage write this test
    // is about is downstream of that wait.
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    // Lifecycle stages run only where a receptor invoker exists; without one the helper skips the
    // stage before deserializing, and the throwing deserializer this test is about is never reached.
    services.AddSingleton<IReceptorInvoker>(new NullReceptorInvoker());

    await using var provider = services.BuildServiceProvider();
    var callback = provider.GetRequiredService<OutboxBulkFlushCallback>();

    await callback(messages, CancellationToken.None);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("the storage call MUST happen even when both lifecycle stages fail — skipping it means the message is gone");
    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(1)
      .Because("the exact batch handed to the callback must reach storage unchanged");

    // Both inline stages ran on this thread, so their entries exist by now; the detached stages log their own failures
    // from the background and are not what this test is about.
    var errors = loggerProvider.Snapshot()
      .Where(e => e.Level == LogLevel.Error)
      .Select(e => e.Message)
      .ToList();
    await Assert.That(errors.Any(m => m.Contains("PreDistributeInline", StringComparison.Ordinal) && m.Contains(messageId, StringComparison.Ordinal))).IsTrue()
      .Because("a pre-store failure that is not logged leaves no trace that a receptor never ran");
    await Assert.That(errors.Any(m => m.Contains("PostDistributeInline", StringComparison.Ordinal) && m.Contains(messageId, StringComparison.Ordinal))).IsTrue()
      .Because("the post-store failure has to name its stage, so a reader knows the batch is already persisted");
  }

  [Test]
  public async Task OutboxBulkFlushCallback_OneMessagesReceptorThrows_EveryOtherMessageStillRunsBothDistributeStagesAsync() {
    // #2310: the batch coalesces messages from many streams (#2220), and one message's failing receptor used to skip the
    // Pre/Distribute and PostDistributeInline stages for every message after it in the batch, logged as one error that
    // named none of them. Now the failure is logged against its own message and every other message still runs.
    var failing = _buildOutboxMessage();
    var healthy = _buildOutboxMessage();
    var invoker = new FailingForMessageInvoker(failing.MessageId);
    var coordinator = new NoOpWorkCoordinator();
    var loggerProvider = new RecordingLoggerProvider();

    var services = _composeWorkerPipeline();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ILifecycleMessageDeserializer>(new ProbeLifecycleMessageDeserializer());
    services.AddSingleton<ILoggerFactory>(new LoggerFactory([loggerProvider]));
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddScoped<IReceptorInvoker>(_ => invoker);

    await using var provider = services.BuildServiceProvider();
    var callback = provider.GetRequiredService<OutboxBulkFlushCallback>();

    await callback([failing, healthy], CancellationToken.None);

    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(2)
      .Because("the store still happens for the whole batch");
    await Assert.That(invoker.InvokedAt(LifecycleStage.PreDistributeInline)).IsEquivalentTo(new[] { failing.MessageId, healthy.MessageId })
      .Because("the healthy message's pre-store receptors run although the failing message's threw");
    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { failing.MessageId, healthy.MessageId })
      .Because("the healthy message's post-store receptors run although the failing message's threw");

    var failures = loggerProvider.Snapshot()
      .Where(e => e.Level == LogLevel.Error && e.Category == "Whizbang.Core.Workers.OutboxBatchFlush")
      .ToList();
    await Assert.That(failures.Count).IsEqualTo(2)
      .Because("one entry per failure, at each inline stage it happened");
    await Assert.That(failures.All(e => e.Message.Contains(failing.MessageId.ToString(), StringComparison.Ordinal))).IsTrue()
      .Because("each entry names the message whose receptor failed");
    await Assert.That(failures.Any(e => e.Message.Contains(healthy.MessageId.ToString(), StringComparison.Ordinal))).IsFalse()
      .Because("no entry blames a message whose receptors succeeded");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceCollection _composeWorkerPipeline() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    return services;
  }

  private static OutboxMessage _buildOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonElement.Parse("{}"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = messageId.Value,
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.PipelineFlushCoverageTestEvent, Whizbang.Core.Component.Tests]], Whizbang.Core",
      MessageType = "Whizbang.Core.Tests.Workers.PipelineFlushCoverageTestEvent"
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  /// <summary>
  /// IPerspectiveSnapshotStore double that always reports no snapshot — the cheapest branch of
  /// StreamCompactor.CompactAsync that is still real end-to-end behavior, not just a type check.
  /// </summary>
  private sealed class NoSnapshotStore : IPerspectiveSnapshotStore {
    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(
      Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid SnapshotEventId, JsonDocument SnapshotData)?>(null);

    public Task CreateSnapshotAsync(
      Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      throw new NotSupportedException();

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      throw new NotSupportedException();

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException();

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      throw new NotSupportedException();

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException();
  }

  /// <summary>
  /// ILifecycleMessageDeserializer double that always throws — simulates a misbehaving
  /// deserializer or receptor so the outbox flush callback's own try/catch around each
  /// lifecycle stage is exercised without needing a real receptor registry.
  /// </summary>
  private sealed class ThrowingLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      throw new InvalidOperationException("Simulated lifecycle deserialize failure");
  }

  /// <summary>
  /// Records each (stage, message) it runs and throws at the inline Distribute stages for one message.
  /// </summary>
  private sealed class FailingForMessageInvoker(Guid failFor) : IReceptorInvoker {
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LifecycleStage Stage, Guid MessageId)> _invoked = new();

    public Guid[] InvokedAt(LifecycleStage stage) => [.. _invoked.Where(i => i.Stage == stage).Select(i => i.MessageId)];

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _invoked.Enqueue((stage, envelope.MessageId.Value));
      if (envelope.MessageId.Value == failFor && stage is LifecycleStage.PreDistributeInline or LifecycleStage.PostDistributeInline) {
        throw new InvalidOperationException($"receptor failed for {failFor}");
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Reads every payload as a <see cref="PipelineFlushCoverageTestEvent"/>.</summary>
  private sealed class ProbeLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new PipelineFlushCoverageTestEvent();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new PipelineFlushCoverageTestEvent();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new PipelineFlushCoverageTestEvent();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new PipelineFlushCoverageTestEvent();
  }

  /// <summary>
  /// Records every log entry, so a test can assert a failure was actually logged, not just "did not throw".
  /// </summary>
  /// <remarks>
  /// The detached lifecycle stages log from background threads while the inline stages log on the test's thread, so
  /// the list is guarded rather than handed out raw: an unsynchronized add racing across threads can drop an entry.
  /// </remarks>
  private sealed class RecordingLoggerProvider : ILoggerProvider {
    private readonly Lock _sync = new();
    private readonly List<(string Category, LogLevel Level, Exception? Exception, string Message)> _entries = [];

    public List<(string Category, LogLevel Level, Exception? Exception, string Message)> Snapshot() {
      lock (_sync) {
        return [.. _entries];
      }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

    public void Dispose() { }

    private void _record(string category, LogLevel level, Exception? exception, string message) {
      lock (_sync) {
        _entries.Add((category, level, exception, message));
      }
    }

    private sealed class RecordingLogger(string category, RecordingLoggerProvider provider) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
        provider._record(category, logLevel, exception, formatter(state, exception));
      }
    }
  }
}

/// <summary>
/// Test event for WorkerPipelineExtensionsCoverageTests' outbox flush callback tests: the envelope-type string, and
/// what the probe deserializer returns for every payload.
/// </summary>
public record PipelineFlushCoverageTestEvent : IEvent {
  [StreamId]
  public string Data { get; init; } = string.Empty;
}
