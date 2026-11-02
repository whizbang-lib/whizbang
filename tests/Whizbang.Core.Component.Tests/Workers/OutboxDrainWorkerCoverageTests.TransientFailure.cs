// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That a failed outbox drain batch costs the batch and nothing else. The per-stream path always
/// isolated its own failures, and the batched fetch degrades to per-stream fetches; what had no
/// guard was the batch envelope around them, whose publish flush runs in a <c>finally</c> and whose
/// security-context establishment can reach a store. Anything thrown there left
/// <c>ExecuteAsync</c> and the host's default <c>StopHost</c> behavior stopped the process. The
/// mirror worker, <see cref="InboxDrainWorker"/>, had the guard all along.
/// </summary>
public partial class OutboxDrainWorkerCoverageTests {
  /// <summary>
  /// A consumer's security-context provider that fails — authority resolution is theirs to
  /// implement and may well read a database, so it is one of the batch envelope's ways to throw.
  /// </summary>
  private sealed class ThrowingSecurityContextProvider(Exception failure) : IMessageSecurityContextProvider {
    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) =>
      throw failure;
  }

  /// <summary>Returns one publishable row on the first fetch, nothing after, and says when.</summary>
  private sealed class OneRowThenEmptyCoordinator(OutboxBatchRow row) : CoordinatorBase {
    private readonly TaskCompletionSource _secondFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetchCalls;

    public int FetchCalls => Volatile.Read(ref _fetchCalls);
    public Task SecondFetch => _secondFetch.Task;

    public override Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes,
        CancellationToken cancellationToken = default) {
      var call = Interlocked.Increment(ref _fetchCalls);
      if (call == 1) {
        return Task.FromResult<IReadOnlyList<OutboxBatchRow>>([row]);
      }
      _secondFetch.TrySetResult();
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>([]);
    }
  }

  /// <summary>
  /// Drains a batch whose publish flush fails, then another that finds nothing, and returns what
  /// the worker logged. The waits are the worker's own report and its next fetch.
  /// </summary>
  private static async Task<EventIdSignalingLogger<OutboxDrainWorker>> _runBatchAfterAFailedFlushAsync(
      Exception flushFailure, int expectedEventId, DatabaseFailureEscalationOptions? escalation = null) {
    var streamId = (Guid)TrackedGuid.New();
    var row = _row((Guid)TrackedGuid.New(), streamId);
    var coord = new OneRowThenEmptyCoordinator(row);
    var drain = new DrainChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new EventIdSignalingLogger<OutboxDrainWorker>();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IMessageSecurityContextProvider>(new ThrowingSecurityContextProvider(flushFailure));
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(),
      drainChannel: drain,
      completionChannel: new CompletionChannel(),
      failureChannel: new FailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      jsonOptions: _jsonOpts,
      logger: logger,
      publishStrategy: new BulkSuccessStrategy(),
      lifecycleMessageDeserializer: new PassthroughDeserializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: OutboxDrainWorker.CreateDefaultGovernor((Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 })).Value),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      databaseFailureEscalation: escalation);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    _ = drain.TryWrite(streamId);
    await logger.WhenLoggedAsync(expectedEventId, TimeSpan.FromSeconds(10));
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("the failed batch must not end the loop: the outbox rows are durable and the claim "
             + "backstop re-offers their streams, while a stopped host publishes nothing at all");

    _ = drain.TryWrite((Guid)TrackedGuid.New());
    await coord.SecondFetch.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(coord.FetchCalls).IsEqualTo(2)
      .Because("the batch after the failure drains as if nothing had happened");
    if (escalation is not null) {
      await logger.WhenLoggedAsync(DatabaseFailureEscalation.RECOVERED_EVENT_ID, TimeSpan.FromSeconds(10));
    }

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
    return logger;
  }

  [Test]
  public async Task DrainBatch_TransientDatabaseFailure_IsNamedAsSuchAndTheLoopTakesTheNextBatchAsync() {
    var logger = await _runBatchAfterAFailedFlushAsync(
      FakeDbException.WithSqlState("40001", message: "could not serialize access"),
      OutboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID);

    var reported = logger.LinesWith(OutboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("#1986: a transient database failure is retried and loses nothing, so each one is a Warning");
    await Assert.That(reported[0].Message)
      .Contains(TransientDatabaseFailure.SERIALIZATION_FAILURE, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("40001", StringComparison.Ordinal);
    await Assert.That(logger.LinesWith(OutboxDrainWorker.BATCH_DRAIN_FAILURE_EVENT_ID)).IsEmpty();
    await Assert.That(logger.Lines.Where(l => l.Level >= LogLevel.Error)).IsEmpty()
      .Because("#1986: one serialization failure is far below the escalation threshold, so it pages nobody");
  }

  [Test]
  public async Task DrainBatch_TransientFailurePastTheThreshold_EscalatesOnceAndSaysWhenItRecoversAsync() {
    var logger = await _runBatchAfterAFailedFlushAsync(
      FakeDbException.WithSqlState("08006", message: "the connection dropped"),
      OutboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID,
      new DatabaseFailureEscalationOptions { ConsecutiveFailures = 1, FailingFor = TimeSpan.Zero });

    var escalated = logger.LinesWith(DatabaseFailureEscalation.ESCALATED_EVENT_ID);
    await Assert.That(escalated).Count().IsEqualTo(1)
      .Because("#1986: the worker reports its transient failures to the escalation, which logs one Error past the threshold");
    await Assert.That(escalated[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(escalated[0].Message).Contains(nameof(OutboxDrainWorker), StringComparison.Ordinal);
    var recovered = logger.LinesWith(DatabaseFailureEscalation.RECOVERED_EVENT_ID);
    await Assert.That(recovered).Count().IsEqualTo(1)
      .Because("#1986: the batch that drains after the failure tells the escalation the database is answering");
    await Assert.That(recovered[0].Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task DrainBatch_FailureThatIsNotTheDatabases_IsReportedAsADefectAndTheLoopContinuesAsync() {
    var logger = await _runBatchAfterAFailedFlushAsync(
      new InvalidOperationException("a defect in the publish flush"),
      OutboxDrainWorker.BATCH_DRAIN_FAILURE_EVENT_ID);

    var reported = logger.LinesWith(OutboxDrainWorker.BATCH_DRAIN_FAILURE_EVENT_ID);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Error)
      .Because("#1986: a failure that is not the database's is a defect and wants fixing, so it stays an Error");
    await Assert.That(reported[0].Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(logger.LinesWith(OutboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID)).IsEmpty()
      .Because("a defect must not be filed as the database's doing, or nobody fixes it");
  }
}
