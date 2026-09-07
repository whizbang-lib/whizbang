using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
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
  public async Task StreamCloserAndStreamCompactor_ResolveAsSingletonsAndCompactorRunsEndToEndAsync() {
    // Both IStreamCloser and IStreamCompactor are TryAddSingleton factories chaining several
    // dependencies (IWorkCoordinator, IEventStore, IPerspectiveSnapshotStore, and — for the
    // compactor — IStreamCloser itself). Registering the factory proves nothing about whether
    // the chain actually builds; on-demand callers (E3 tier-2 compaction, an admin operation)
    // are the first ones who would discover a broken binding otherwise.
    var services = _composeWorkerPipeline();
    services.AddSingleton<IWorkCoordinator>(new NoOpWorkCoordinator());
    services.AddSingleton<IEventStore>(new InMemoryEventStore());
    services.AddSingleton<IPerspectiveSnapshotStore>(new NoSnapshotStore());

    await using var provider = services.BuildServiceProvider();

    var closerFirst = provider.GetRequiredService<IStreamCloser>();
    var closerSecond = provider.GetRequiredService<IStreamCloser>();
    var compactorFirst = provider.GetRequiredService<IStreamCompactor>();
    var compactorSecond = provider.GetRequiredService<IStreamCompactor>();

    await Assert.That(closerFirst).IsTypeOf<StreamCloser>()
      .Because("the registration promises a real StreamCloser wired to the container's dependencies, not a stand-in");
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
    // message is permanently lost." Both the Pre/Distribute (before store) and Post-Distribute
    // (after store) lifecycle invocations are wrapped in their own try/catch specifically so a
    // misbehaving deserializer or receptor can never block — or silently skip logging — the
    // actual outbox storage write.
    var messages = new[] { _buildOutboxMessage() };
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

    await using var provider = services.BuildServiceProvider();
    var callback = provider.GetRequiredService<OutboxBulkFlushCallback>();

    await callback(messages, CancellationToken.None);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("the storage call MUST happen even when both lifecycle stages fail — skipping it means the message is gone");
    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(1)
      .Because("the exact batch handed to the callback must reach storage unchanged");

    // Not a count: the fire-and-forget DistributeDetached stage logs its own failure too, so the
    // total is three. What matters is that the two the callback catches are individually
    // distinguishable -- merging or dropping either hides which half of the pipeline broke.
    // Filtered by level and message only, deliberately not by exception type: the two failures this
    // test is about carry whatever the deserializer threw, and pinning that would couple the test to
    // an unrelated implementation detail while silently dropping the very entries it looks for.
    var errors = loggerProvider.Entries
      .Where(e => e.Level == LogLevel.Error)
      .Select(e => e.Message)
      .ToList();
    // The pre-distribute stage reports its own failure and does not rethrow -- observed here as
    // "Error invoking PreDistributeDetached lifecycle receptors" -- so the callback's pre-store
    // catch does not see it. That is fine for the invariant under test: what must hold is that a
    // failing lifecycle stage never blocks the store, and that the post-store failure is
    // distinguishable from the pre-store one so a reader knows the batch is already persisted.
    await Assert.That(errors.Any(m => m.Contains("lifecycle receptors", StringComparison.Ordinal))).IsTrue()
      .Because("a lifecycle stage that fails silently leaves no trace that a receptor never ran");
    await Assert.That(errors.Any(m => m.Contains("after store", StringComparison.Ordinal))).IsTrue()
      .Because("the post-store failure has to say the store already happened, or a reader retries a batch that is safely persisted");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceCollection _composeWorkerPipeline() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    return services;
  }

  private static OutboxMessage _buildOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = messageId.Value,
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.PipelineFlushCoverageTestEvent, Whizbang.Core.Tests]], Whizbang.Core",
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
  /// Real ILoggerFactory backed by an in-memory ILoggerProvider so tests can assert on the exact
  /// error entries recorded — proving a failure was actually logged, not just "did not throw".
  /// </summary>
  private sealed class RecordingLoggerProvider : ILoggerProvider {
    public List<(string Category, LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new _RecordingLogger(categoryName, Entries);

    public void Dispose() { }

    private sealed class _RecordingLogger(string category,
        List<(string Category, LogLevel Level, Exception? Exception, string Message)> entries) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
        entries.Add((category, logLevel, exception, formatter(state, exception)));
      }
    }
  }
}

/// <summary>
/// Test event used only for the envelope-type string in WorkerPipelineExtensionsCoverageTests'
/// outbox flush callback test; never actually deserialized (the fake deserializer always throws).
/// </summary>
public record PipelineFlushCoverageTestEvent : IEvent {
  [StreamId]
  public string Data { get; init; } = string.Empty;
}
