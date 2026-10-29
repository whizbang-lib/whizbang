// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// #1871: <see cref="OutboxFlushFailureRecorder"/>, the registered <see cref="OutboxFlushFailureCallback"/>. It keeps a
/// batch the outbox batcher could not store: writes it to the dead-letter table through the coordinator, counts it as
/// dead-letter arrivals, and tells every <see cref="IOutboxFlushFailureObserver"/>, without throwing for a failed
/// write or a failing observer.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxFlushFailureRecorder.cs</code-under-test>
public class OutboxFlushFailureCallbackTests {
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
    var failure = _failure(OutboxFlushFailureCause.RetriesExhausted, new TimeoutException("pool exhausted"), 2);

    var kept = await sut.RecordAsync(failure, CancellationToken.None);

    await Assert.That(kept).IsTrue();
    var (messages, request) = coordinator.Calls.Single();
    await Assert.That(messages.Select(m => m.MessageId)).IsEquivalentTo(failure.Messages.Select(m => m.MessageId));
    await Assert.That(request.FailureReason).IsEqualTo(MessageFailureReason.OutboxStoreFailed);
    await Assert.That(request.Attempts).IsEqualTo(5);
    await Assert.That(request.InstanceId).IsEqualTo(instanceId);
    await Assert.That(request.Generation).IsEqualTo("gen-7");
    await Assert.That(request.ErrorText).Contains("pool exhausted");
    var added = recorded.GetByName("whizbang.dead_letters.added").Where(m => m.Tags.ContainsKey(DeadLetterMetrics.REASON_TAG)).ToList();
    await Assert.That(added.Sum(m => m.Value)).IsEqualTo(2);
    await Assert.That(added.All(m => m.Tags[DeadLetterMetrics.REASON_TAG] == nameof(MessageFailureReason.OutboxStoreFailed))).IsTrue();
    var told = observer.Failures.Single();
    await Assert.That(told.DeadLettered).IsTrue();
    await Assert.That(told.Cause).IsEqualTo(OutboxFlushFailureCause.RetriesExhausted);
  }

  [Test]
  public async Task RecordAsync_AbandonedWithoutAnError_RecordsWhyAndDefaultsTheAuditColumnsAsync() {
    var coordinator = new DeadLetteringCoordinator();
    var sut = _recorder(coordinator, []);

    var kept = await sut.RecordAsync(_failure(OutboxFlushFailureCause.AbandonedAtShutdown, null, 1, attempts: 0), CancellationToken.None);

    await Assert.That(kept).IsTrue().Because("no metrics are registered, and the write still counts as kept");
    var (_, request) = coordinator.Calls.Single();
    await Assert.That(request.ErrorText).Contains("abandoned at shutdown");
    await Assert.That(request.Attempts).IsEqualTo(0);
    await Assert.That(request.InstanceId).IsEqualTo(Guid.Empty);
    await Assert.That(request.Generation).IsEqualTo("unknown");
  }

  [Test]
  public async Task RecordAsync_DeadLetterWriteFails_IsLoggedAndObserversAreToldItWasNotKeptAsync() {
    var coordinator = new DeadLetteringCoordinator { Failure = new InvalidOperationException("database unreachable") };
    var observer = new RecordingObserver();
    var log = new ErrorLog();
    var sut = _recorder(coordinator, [observer], logger: log);

    var kept = await sut.RecordAsync(_failure(OutboxFlushFailureCause.NotRetryable, new InvalidOperationException("refused"), 1), CancellationToken.None);

    await Assert.That(kept).IsFalse();
    await Assert.That(observer.Failures.Single().DeadLettered).IsFalse();
    await Assert.That(log.Errors.Single()).Contains("wh_dead_letters");
  }

  [Test]
  public async Task RecordAsync_CoordinatorWithoutADeadLetterTable_ReportsNotKeptAsync() {
    var sut = _recorder(new NoOpWorkCoordinator(), []);

    var kept = await sut.RecordAsync(_failure(OutboxFlushFailureCause.NotRetryable, new InvalidOperationException("refused"), 1), CancellationToken.None);

    await Assert.That(kept).IsFalse().Because("the interface's default write keeps nothing");
  }

  [Test]
  public async Task RecordAsync_AnObserverThrows_TheOthersAreStillToldAndItIsLoggedAsync() {
    var later = new RecordingObserver();
    var log = new ErrorLog();
    var sut = _recorder(new DeadLetteringCoordinator(), [new ThrowingObserver(), later], logger: log);

    var kept = await sut.RecordAsync(_failure(OutboxFlushFailureCause.RetriesExhausted, new TimeoutException(), 1), CancellationToken.None);

    await Assert.That(kept).IsTrue();
    await Assert.That(later.Failures).Count().IsEqualTo(1);
    await Assert.That(log.Errors.Single()).Contains(nameof(ThrowingObserver));
  }

  private static OutboxFlushFailureRecorder _recorder(
      IWorkCoordinator coordinator,
      IOutboxFlushFailureObserver[] observers,
      IServiceInstanceProvider? instanceProvider = null,
      IGenerationProvider? generationProvider = null,
      DeadLetterMetrics? deadLetterMetrics = null,
      WorkCoordinatorGate? gate = null,
      ILogger<OutboxFlushFailureRecorder>? logger = null) {
    var services = new ServiceCollection().AddScoped(_ => coordinator).BuildServiceProvider();
    return new OutboxFlushFailureRecorder(
      services.GetRequiredService<IServiceScopeFactory>(),
      gate,
      instanceProvider,
      generationProvider,
      deadLetterMetrics,
      observers,
      logger ?? NullLogger<OutboxFlushFailureRecorder>.Instance);
  }

  private static OutboxFlushFailure _failure(OutboxFlushFailureCause cause, Exception? error, int count, int attempts = 5) {
    var stream = (Guid)TrackedGuid.New();
    var messages = Enumerable.Range(0, count).Select(_ => {
      var id = (Guid)TrackedGuid.New();
      return new OutboxMessage {
        MessageId = id,
        StreamId = stream,
        Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse("{}"), []),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      };
    }).ToList();
    return new OutboxFlushFailure(stream, messages, cause, attempts, error);
  }

  private sealed class FixedGeneration(string generation) : IGenerationProvider {
    public string GetGeneration() => generation;
  }

  private sealed class RecordingObserver : IOutboxFlushFailureObserver {
    private readonly ConcurrentQueue<OutboxFlushFailure> _failures = new();

    public IReadOnlyList<OutboxFlushFailure> Failures => [.. _failures];

    public ValueTask OnOutboxFlushFailedAsync(OutboxFlushFailure failure, CancellationToken cancellationToken) {
      _failures.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowingObserver : IOutboxFlushFailureObserver {
    public ValueTask OnOutboxFlushFailedAsync(OutboxFlushFailure failure, CancellationToken cancellationToken) =>
      ValueTask.FromException(new InvalidOperationException("observer broke"));
  }

  private sealed class ErrorLog : ILogger<OutboxFlushFailureRecorder> {
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
    private readonly ConcurrentQueue<(OutboxMessage[] Messages, OutboxDeadLetterRequest Request)> _calls = new();

    public Exception? Failure { get; init; }

    public IReadOnlyList<(OutboxMessage[] Messages, OutboxDeadLetterRequest Request)> Calls => [.. _calls];

    public Task<bool> StoreOutboxDeadLettersAsync(OutboxMessage[] messages, OutboxDeadLetterRequest request, CancellationToken cancellationToken = default) {
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
