// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1885: <see cref="InboxFlushFailureRecorder"/>, the registered <see cref="InboxFlushFailureCallback"/>. It keeps a
/// batch the inbox batcher could not store: writes it to the dead-letter table through the coordinator, counts it as
/// inbox dead-letter arrivals, and tells every <see cref="IInboxFlushFailureObserver"/>, without throwing for a failed
/// write or a failing observer.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InboxFlushFailureRecorder.cs</code-under-test>
public class InboxFlushFailureRecorderTests {
  [Test]
  public async Task RecordAsync_CoordinatorDeadLetters_CountsTheArrivalsAndTellsObserversItWasKeptAsync() {
    var coordinator = new DeadLetteringCoordinator();
    var observer = new RecordingObserver();
    var instanceId = (Guid)TrackedGuid.New();
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    var deadLetterMetrics = new DeadLetterMetrics(new WhizbangMetrics(meterFactory: factory));
    using var recorded = new MetricAssertionHelper(factory.Create(DeadLetterMetrics.METER_NAME));
    var sut = _recorder(coordinator, [observer],
      instanceProvider: new ServiceInstanceProvider(instanceId, "svc", "host", 1),
      generationProvider: new FixedGeneration("gen-7"),
      deadLetterMetrics: deadLetterMetrics,
      gate: new WorkCoordinatorGate(4, NullLogger<WorkCoordinatorGate>.Instance));
    var failure = _failure(InboxFlushFailureCause.RetriesExhausted, new TimeoutException("pool exhausted"), 2);

    var kept = await sut.RecordAsync(failure, CancellationToken.None);

    await Assert.That(kept).IsTrue();
    var (messages, request) = coordinator.Calls.Single();
    await Assert.That(messages.Select(m => m.MessageId)).IsEquivalentTo(failure.Messages.Select(m => m.MessageId));
    await Assert.That(request.FailureReason).IsEqualTo(MessageFailureReason.InboxStoreFailed);
    await Assert.That(request.Attempts).IsEqualTo(5);
    await Assert.That(request.InstanceId).IsEqualTo(instanceId);
    await Assert.That(request.Generation).IsEqualTo("gen-7");
    await Assert.That(request.ErrorText).Contains("pool exhausted");
    var added = recorded.GetByName("whizbang.dead_letters.added").Where(m => m.Tags.ContainsKey(DeadLetterMetrics.REASON_TAG)).ToList();
    await Assert.That(added.Sum(m => m.Value)).IsEqualTo(2);
    await Assert.That(added.All(m => m.Tags[DeadLetterMetrics.REASON_TAG] == nameof(MessageFailureReason.InboxStoreFailed))).IsTrue();
    await Assert.That(added.All(m => m.Tags[DeadLetterMetrics.SOURCE_TABLE_TAG] == DeadLetterSourceTable.INBOX)).IsTrue()
      .Because("the messages are kept as inbox dead letters, which recovery re-enters through the inbox store");
    var told = observer.Failures.Single();
    await Assert.That(told.DeadLettered).IsTrue();
    await Assert.That(told.Cause).IsEqualTo(InboxFlushFailureCause.RetriesExhausted);
  }

  [Test]
  public async Task Constructor_NullCollaborator_ThrowsAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var instance = new ServiceInstanceProvider(Guid.Empty, "svc", "host", 1);
    var generation = new FixedGeneration("gen-0");
    var logger = NullLogger<InboxFlushFailureRecorder>.Instance;
    await Assert.That(() => new InboxFlushFailureRecorder(null!, null, instance, generation, null, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InboxFlushFailureRecorder(scopes, null, null!, generation, null, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InboxFlushFailureRecorder(scopes, null, instance, null!, null, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InboxFlushFailureRecorder(scopes, null, instance, generation, null, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InboxFlushFailureRecorder(scopes, null, instance, generation, null, [], null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _recorder(new NoOpWorkCoordinator(), []).RecordAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RecordAsync_AbandonedWithoutAnError_RecordsWhyItWasNotStoredAsync() {
    var coordinator = new DeadLetteringCoordinator();
    var sut = _recorder(coordinator, []);

    var kept = await sut.RecordAsync(_failure(InboxFlushFailureCause.AbandonedAtShutdown, null, 1, attempts: 0), CancellationToken.None);

    await Assert.That(kept).IsTrue().Because("no metrics are registered, and the write still counts as kept");
    var (_, request) = coordinator.Calls.Single();
    await Assert.That(request.ErrorText).Contains("abandoned at shutdown");
    await Assert.That(request.Attempts).IsEqualTo(0);
  }

  [Test]
  public async Task RecordAsync_DeadLetterWriteFails_IsLoggedAndObserversAreToldItWasNotKeptAsync() {
    var coordinator = new DeadLetteringCoordinator { Failure = new InvalidOperationException("database unreachable") };
    var observer = new RecordingObserver();
    var log = new ErrorLog();
    var sut = _recorder(coordinator, [observer], logger: log);

    var kept = await sut.RecordAsync(_failure(InboxFlushFailureCause.NotRetryable, new InvalidOperationException("refused"), 1), CancellationToken.None);

    await Assert.That(kept).IsFalse();
    await Assert.That(observer.Failures.Single().DeadLettered).IsFalse();
    await Assert.That(log.Errors.Single()).Contains("wh_dead_letters");
  }

  [Test]
  public async Task RecordAsync_CoordinatorWithoutADeadLetterTable_ReportsNotKeptAsync() {
    var sut = _recorder(new NoOpWorkCoordinator(), []);

    var kept = await sut.RecordAsync(_failure(InboxFlushFailureCause.NotRetryable, new InvalidOperationException("refused"), 1), CancellationToken.None);

    await Assert.That(kept).IsFalse().Because("the interface's default write keeps nothing");
  }

  [Test]
  public async Task RecordAsync_AnObserverThrows_TheOthersAreStillToldAndItIsLoggedAsync() {
    var later = new RecordingObserver();
    var log = new ErrorLog();
    var sut = _recorder(new DeadLetteringCoordinator(), [new ThrowingObserver(), later], logger: log);

    var kept = await sut.RecordAsync(_failure(InboxFlushFailureCause.RetriesExhausted, new TimeoutException(), 1), CancellationToken.None);

    await Assert.That(kept).IsTrue();
    await Assert.That(later.Failures).Count().IsEqualTo(1);
    await Assert.That(log.Errors.Single(e => e.Contains(nameof(ThrowingObserver), StringComparison.Ordinal))).IsNotNull();
  }

  // #1901: every message the recorder keeps gets the dead-letter record, at Error by default, with the store failure.

  [Test]
  public async Task RecordAsync_CoordinatorDeadLetters_LogsOneDeadLetterRecordPerMessageAtErrorAsync() {
    var logger = new FakeLogger<InboxFlushFailureRecorder>();
    var sut = _recorder(new DeadLetteringCoordinator(), [], logger: logger);
    var failure = _failure(InboxFlushFailureCause.RetriesExhausted, new TimeoutException("pool exhausted"), 2);

    await sut.RecordAsync(failure, CancellationToken.None);

    var records = logger.Collector.GetSnapshot().Where(r => r.Id.Id == 1901).ToList();
    await Assert.That(records).Count().IsEqualTo(2)
      .Because("each message that could not be stored is its own dead letter and gets its own record");
    foreach (var (record, message) in records.Zip(failure.Messages)) {
      await Assert.That(record.Level).IsEqualTo(LogLevel.Error)
        .Because("#1901: a message the batcher could not store is dead-lettered, logged at Error by default");
      await Assert.That(record.Exception).IsTypeOf<TimeoutException>()
        .Because("the store failure is the last failure's exception, so the record carries it");
      await Assert.That(record.Message).Contains(message.MessageId.ToString())
        .And.Contains(message.MessageType)
        .And.Contains(nameof(MessageFailureReason.InboxStoreFailed));
    }
  }

  [Test]
  public async Task RecordAsync_AbandonedWithoutAnError_RecordSaysWhyAsync() {
    var logger = new FakeLogger<InboxFlushFailureRecorder>();
    var sut = _recorder(new DeadLetteringCoordinator(), [], logger: logger);

    await sut.RecordAsync(_failure(InboxFlushFailureCause.AbandonedAtShutdown, null, 1, attempts: 0), CancellationToken.None);

    var record = logger.Collector.GetSnapshot().Single(r => r.Id.Id == 1901);
    await Assert.That(record.Exception).IsNull();
    await Assert.That(record.Message).Contains("abandoned at shutdown")
      .Because("with no exception the record's last error is the reason the messages were not stored");
  }

  [Test]
  public async Task RecordAsync_DeadLetterWriteFails_LogsNoDeadLetterRecordAsync() {
    var logger = new FakeLogger<InboxFlushFailureRecorder>();
    var sut = _recorder(new DeadLetteringCoordinator { Failure = new InvalidOperationException("database unreachable") }, [], logger: logger);

    await sut.RecordAsync(_failure(InboxFlushFailureCause.NotRetryable, new InvalidOperationException("refused"), 1), CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Id.Id == 1901)).IsFalse()
      .Because("nothing reached the dead-letter table, so no message was dead-lettered; the failed write is logged on its own");
  }

  [Test]
  public async Task RecordAsync_ConfiguredWarning_LogsTheRecordAtWarningAsync() {
    var logger = new FakeLogger<InboxFlushFailureRecorder>();
    var sut = _recorder(new DeadLetteringCoordinator(), [], logger: logger,
      deadLetterLogging: new DeadLetterLoggingOptions { Level = LogLevel.Warning });

    await sut.RecordAsync(_failure(InboxFlushFailureCause.RetriesExhausted, new TimeoutException("pool exhausted"), 1), CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot().Single(r => r.Id.Id == 1901).Level).IsEqualTo(LogLevel.Warning);
  }

  private static InboxFlushFailureRecorder _recorder(
      IWorkCoordinator coordinator,
      IInboxFlushFailureObserver[] observers,
      IServiceInstanceProvider? instanceProvider = null,
      IGenerationProvider? generationProvider = null,
      DeadLetterMetrics? deadLetterMetrics = null,
      WorkCoordinatorGate? gate = null,
      ILogger<InboxFlushFailureRecorder>? logger = null,
      DeadLetterLoggingOptions? deadLetterLogging = null) {
    var services = new ServiceCollection().AddScoped(_ => coordinator).BuildServiceProvider();
    return new InboxFlushFailureRecorder(
      services.GetRequiredService<IServiceScopeFactory>(),
      gate,
      instanceProvider ?? new ServiceInstanceProvider(Guid.Empty, "svc", "host", 1),
      generationProvider ?? new FixedGeneration("gen-0"),
      deadLetterMetrics,
      observers,
      logger ?? NullLogger<InboxFlushFailureRecorder>.Instance,
      deadLetterLogging);
  }

  private static InboxFlushFailure _failure(InboxFlushFailureCause cause, Exception? error, int count, int attempts = 5) {
    var stream = (Guid)TrackedGuid.New();
    var messages = Enumerable.Range(0, count).Select(_ => {
      var id = (Guid)TrackedGuid.New();
      return new InboxMessage {
        MessageId = id,
        HandlerName = "test",
        StreamId = stream,
        Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse("{}"), []),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      };
    }).ToList();
    return new InboxFlushFailure(stream, messages, cause, attempts, error);
  }

  private sealed class FixedGeneration(string generation) : IGenerationProvider {
    public string GetGeneration() => generation;
  }

  private sealed class RecordingObserver : IInboxFlushFailureObserver {
    private readonly ConcurrentQueue<InboxFlushFailure> _failures = new();

    public IReadOnlyList<InboxFlushFailure> Failures => [.. _failures];

    public ValueTask OnInboxFlushFailedAsync(InboxFlushFailure failure, CancellationToken cancellationToken) {
      _failures.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowingObserver : IInboxFlushFailureObserver {
    public ValueTask OnInboxFlushFailedAsync(InboxFlushFailure failure, CancellationToken cancellationToken) =>
      ValueTask.FromException(new InvalidOperationException("observer broke"));
  }

  private sealed class ErrorLog : ILogger<InboxFlushFailureRecorder> {
    private readonly ConcurrentQueue<string> _errors = new();

    public IReadOnlyList<string> Errors => [.. _errors];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Error) {
        _errors.Enqueue(formatter(state, exception));
      }
    }
  }

  /// <summary>Keeps what it is asked to dead-letter, or fails the write.</summary>
  private sealed class DeadLetteringCoordinator : IWorkCoordinator {
    private readonly ConcurrentQueue<(InboxMessage[] Messages, InboxDeadLetterRequest Request)> _calls = new();

    public Exception? Failure { get; init; }

    public IReadOnlyList<(InboxMessage[] Messages, InboxDeadLetterRequest Request)> Calls => [.. _calls];

    public Task<bool> StoreInboxDeadLettersAsync(InboxMessage[] messages, InboxDeadLetterRequest request, CancellationToken cancellationToken = default) {
      if (Failure is not null) {
        return Task.FromException<bool>(Failure);
      }
      _calls.Enqueue((messages, request));
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
