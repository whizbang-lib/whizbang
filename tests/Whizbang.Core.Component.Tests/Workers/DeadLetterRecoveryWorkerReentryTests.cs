// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.Workers;

/// <summary>
/// The recovery worker re-enters an inbox or broker dead letter through the inbox store, with the row a delivery of
/// the same envelope is built as, naming its dead letter (#1664). Every other row is re-driven as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
public class DeadLetterRecoveryWorkerReentryTests {
  private const string EVENT_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";

  [Test]
  [Arguments(DeadLetterSourceTable.BROKER)]
  [Arguments(DeadLetterSourceTable.INBOX)]
  public async Task AnInboxOrBrokerDeadLetter_IsStoredThroughTheInbox_NamingItsDeadLetterAsync(string sourceTable) {
    var fake = new ReadingFakeStore();
    var entry = _entry(sourceTable);
    fake.Redelivery = new DeadLetterRedelivery(entry.DeadLetterId, sourceTable, entry.SourceId, EVENT_TYPE, _wire(entry.SourceId));
    fake.Batches.Enqueue([entry]);

    var worker = await _scanOnceAsync(fake, registerCoordinator: true, partitionCount: 37);

    await Assert.That(fake.Stored).Count().IsEqualTo(1);
    var (message, partitionCount) = fake.Stored[0];
    await Assert.That(message.MessageId).IsEqualTo(entry.SourceId);
    await Assert.That(message.RecoveredFromDeadLetterId).IsEqualTo(entry.DeadLetterId);
    await Assert.That(partitionCount).IsEqualTo(37)
      .Because("the row is stored with the partition count every arrival is stored with");
    await Assert.That(fake.RecoverCalls).IsEmpty()
      .Because("an inbox or broker dead letter no longer re-enters through recover_dead_letter's own writes");
    await Assert.That(worker.TotalRecovered).IsEqualTo(1L);
  }

  [Test]
  public async Task WithoutClaimWorkerOptions_TheRowIsStoredWithTheDefaultPartitionCountAsync() {
    var fake = new ReadingFakeStore();
    var entry = _entry(DeadLetterSourceTable.BROKER);
    fake.Redelivery = new DeadLetterRedelivery(entry.DeadLetterId, DeadLetterSourceTable.BROKER, entry.SourceId, EVENT_TYPE, _wire(entry.SourceId));
    fake.Batches.Enqueue([entry]);

    _ = await _scanOnceAsync(fake, registerCoordinator: true, partitionCount: null);

    await Assert.That(fake.Stored).Count().IsEqualTo(1);
    await Assert.That(fake.Stored[0].PartitionCount).IsEqualTo(new ClaimWorkerOptions().PartitionCount)
      .Because("a host that never configures the claim workers stores every arrival with their default partition count");
  }

  [Test]
  public async Task AServiceThatCannotReadTheEnvelope_ReDrivesTheRowItselfAsync() {
    // The interface's default read answers null, as a recovery service written before #1664 does.
    var fake = new FakeStore();
    var entry = _entry(DeadLetterSourceTable.BROKER);
    fake.Batches.Enqueue([entry]);

    var worker = await _scanOnceAsync(fake, registerCoordinator: true, partitionCount: null);

    await Assert.That(fake.RecoverCalls).IsEquivalentTo([entry.DeadLetterId]);
    await Assert.That(fake.Stored).IsEmpty();
    await Assert.That(worker.TotalRecovered).IsEqualTo(1L);
  }

  [Test]
  public async Task AnOutboxDeadLetter_IsReDrivenAsBeforeAsync() {
    var fake = new ReadingFakeStore();
    var entry = _entry(DeadLetterSourceTable.OUTBOX);
    fake.Batches.Enqueue([entry]);

    _ = await _scanOnceAsync(fake, registerCoordinator: true, partitionCount: null);

    await Assert.That(fake.RecoverCalls).IsEquivalentTo([entry.DeadLetterId]);
    await Assert.That(fake.ReadCalls).IsEmpty();
  }

  [Test]
  public async Task AStoredEnvelopeThatCannotBeRead_IsHeldForReview_NotRetriedAsync() {
    var fake = new ReadingFakeStore();
    var entry = _entry(DeadLetterSourceTable.BROKER);
    fake.Redelivery = new DeadLetterRedelivery(entry.DeadLetterId, DeadLetterSourceTable.BROKER, entry.SourceId, EVENT_TYPE, "{not json");
    fake.Batches.Enqueue([entry]);

    var worker = await _scanOnceAsync(fake, registerCoordinator: true, partitionCount: null);

    await Assert.That(fake.HoldCalls).IsEquivalentTo([entry.DeadLetterId])
      .Because("the same bytes fail the same way on every retry of this build");
    await Assert.That(fake.Stored).IsEmpty();
    await Assert.That(fake.ScheduleCalls).IsEmpty();
    await Assert.That(worker.TotalHeld).IsEqualTo(1L);
    await Assert.That(worker.TotalRecovered).IsEqualTo(0L);
  }

  [Test]
  public async Task WithoutAWorkCoordinator_TheAttemptIsScheduledAgainAsync() {
    var fake = new ReadingFakeStore();
    var entry = _entry(DeadLetterSourceTable.BROKER);
    fake.Redelivery = new DeadLetterRedelivery(entry.DeadLetterId, DeadLetterSourceTable.BROKER, entry.SourceId, EVENT_TYPE, _wire(entry.SourceId));
    fake.Batches.Enqueue([entry]);

    var worker = await _scanOnceAsync(fake, registerCoordinator: false, partitionCount: null);

    await Assert.That(fake.ScheduleCalls).IsEquivalentTo([entry.DeadLetterId]);
    await Assert.That(worker.TotalRecovered).IsEqualTo(0L);
  }

  // ===== Helpers =====

  private static DeadLetterEntry _entry(string sourceTable) => new(
    DeadLetterId: (Guid)TrackedGuid.New(),
    SourceTable: sourceTable,
    SourceId: (Guid)TrackedGuid.New(),
    StreamId: null,
    MessageType: EVENT_TYPE,
    FailureReason: MessageFailureReason.BrokerDeadLetter,
    AttemptsWhenDlq: 1,
    DeadLetteredAt: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
    RecoveryStatus: DeadLetterRecoveryStatus.Pending,
    RecoveryAttempts: 0,
    Generation: "test/0.0.1");

  private static string _wire(Guid messageId) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return JsonSerializer.Serialize(new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    }, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
  }

  private static async Task<DeadLetterRecoveryWorker> _scanOnceAsync(FakeStore fake, bool registerCoordinator, int? partitionCount) {
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(fake);
    if (registerCoordinator) {
      services.AddSingleton<IWorkCoordinator>(fake);
    }
    if (partitionCount is { } count) {
      services.AddSingleton(Options.Create(new ClaimWorkerOptions { PartitionCount = count }));
    }
    services.AddSingleton<IEventTypeProvider>(NullEventTypeProvider.Instance);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    await using var provider = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new DeadLetterRecoveryWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: gate,
      options: Options.Create(new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50, EnableGenerationReplay = false }),
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      generationProvider: new DefaultGenerationProvider(),
      logger: NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: new NoOpWorkNotificationListener());
    var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnScanCompleted += () => scanned.TrySetResult();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await scanned.Task.WaitAsync(TimeSpan.FromSeconds(30));
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
    return worker;
  }

  /// <summary>A recovery service that reads the stored envelope back.</summary>
  private sealed class ReadingFakeStore : FakeStore, IDeadLetterRecoveryService {
    public DeadLetterRedelivery? Redelivery { get; set; }
    public List<Guid> ReadCalls { get; } = [];
    public Task<DeadLetterRedelivery?> ReadRedeliveryAsync(Guid deadLetterId, CancellationToken ct = default) {
      ReadCalls.Add(deadLetterId);
      return Task.FromResult(Redelivery);
    }
  }

  /// <summary>The recovery service and the inbox store, recording what the worker asked of them. It keeps the
  /// interface's default envelope read.</summary>
  private class FakeStore : IDeadLetterRecoveryService, IWorkCoordinator {
    public Queue<List<DeadLetterEntry>> Batches { get; } = new();
    public List<Guid> RecoverCalls { get; } = [];
    public List<Guid> HoldCalls { get; } = [];
    public List<Guid> ScheduleCalls { get; } = [];
    public List<(InboxMessage Message, int PartitionCount)> Stored { get; } = [];

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<DeadLetterEntry>>(Batches.Count > 0 ? Batches.Dequeue() : []);
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
      RecoverCalls.Add(deadLetterId);
      return Task.FromResult(true);
    }
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      HoldCalls.Add(deadLetterId);
      return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      ScheduleCalls.Add(deadLetterId);
      return Task.CompletedTask;
    }
    public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>([]);
    public Task<int> RecordStacksAsync(IReadOnlyList<(Guid, Whizbang.Core.DeadLetters.StackIdentity)> entries, CancellationToken ct = default) =>
      Task.FromResult(entries.Count);
    public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([]);
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(new CanaryVerdict(CanaryVerdictKind.Pass, 0, 0, 0));
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      foreach (var message in messages) {
        Stored.Add((message, partitionCount));
      }
      return Task.CompletedTask;
    }
    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<ServiceBacklog?>(null);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
