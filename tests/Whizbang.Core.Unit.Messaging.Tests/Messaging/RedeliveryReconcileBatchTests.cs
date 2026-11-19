// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The maintainer's drain reconciles a backlog of recorded redelivery reconciles as a batch (#2099): one store call for
/// every leased reconcile, so each sealed digest epoch the batch touches is refolded once, and then each bundle is
/// concluded as the single reconcile concludes it. A store without the batch call reconciles each bundle on its own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryReconciliation.cs</code-under-test>
/// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
public class RedeliveryReconcileBatchTests {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private const string ORIGIN_NAME = "orders-service";

  [Test]
  public async Task Batch_WithABatchCapableStore_ReconcilesEveryBundleInOneCall_AndConcludesEachAsync() {
    var origin = TrackedGuid.New().Value;
    var (adoptedStream, unchangedStream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var (adoptedEvent, unchangedEvent) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      BatchAnswer = _ => [
        [new RedeliveredBucketPresence(adoptedStream, null, EVENT_TYPE, 1, 1, OriginsFilled: 0)],
        [new RedeliveredBucketPresence(unchangedStream, null, EVENT_TYPE, 1, 0, OriginsFilled: 0)],
      ],
    };
    var ledger = new IntegrityRepairLedger();
    var unchangedKey = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, unchangedStream);
    _ = await ledger.TryBeginReportAsync(unchangedKey, 1, 2, 3, 4, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5));
    var logs = new CapturingLoggerFactory();
    await using var services = _services(coordinator, ledger, logs);

    await RedeliveryReconciler.HandleDeferredBatchAsync(
      [_payload(origin, ORIGIN_NAME, adoptedEvent, 5, allPresent: true), _payload(origin, null, unchangedEvent, null, allPresent: true)],
      services, CancellationToken.None);

    var batch = coordinator.BatchReconciles.Single();
    await Assert.That(batch.Select(b => (b.OriginServiceId, b.OriginServiceName, b.EventIds.Single(), b.CommitSequences.Single())))
      .IsEquivalentTo([(origin, (string?)ORIGIN_NAME, adoptedEvent, (long?)5), (origin, (string?)null, unchangedEvent, (long?)null)])
      .Because("every recorded reconcile goes to the store in one call, in arrival order, with what was recorded");
    await Assert.That(coordinator.Adoptions).IsEmpty().Because("no bundle is reconciled on its own when the batch call answers");
    await Assert.That(await ledger.TryBeginRepairAsync(unchangedKey, DateTimeOffset.UnixEpoch.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("the bundle that stored and adopted nothing is concluded as the single reconcile concludes it");
    await Assert.That(logs.Messages.Count(m => m.Contains("only the origin sequence of 1", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the adopted event already carried its origin, so it is reported as a sequence fill, not an origin fill");
  }

  [Test]
  public async Task Batch_WithoutTheBatchCall_ReconcilesEachBundleOnItsOwnAsync() {
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    await using var services = _services(coordinator, ledger: null, logs: null);

    await RedeliveryReconciler.HandleDeferredBatchAsync(
      [_payload(TrackedGuid.New().Value, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: null),
       _payload(TrackedGuid.New().Value, ORIGIN_NAME, TrackedGuid.New().Value, 2, allPresent: false)],
      services, CancellationToken.None);

    await Assert.That(coordinator.BatchReconciles.Count).IsEqualTo(1).Because("precondition: the batch call was offered");
    await Assert.That(coordinator.Adoptions.Count).IsEqualTo(2)
      .Because("a store that cannot reconcile a batch reconciles each bundle as before");
  }

  [Test]
  public async Task Batch_WithoutACoordinator_DoesNothingAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await RedeliveryReconciler.HandleDeferredBatchAsync(
      [_payload(TrackedGuid.New().Value, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: true)], services,
      CancellationToken.None);

    await Assert.That(services.GetService<IWorkCoordinator>()).IsNull();
  }

  [Test]
  public async Task Batch_AnUnreadablePayload_FaultsTheBatch_SoTheDrainHandlesEachOnItsOwnAsync() {
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    await using var services = _services(coordinator, ledger: null, logs: null);

    await Assert.That(async () => await RedeliveryReconciler.HandleDeferredBatchAsync(
        [_payload(TrackedGuid.New().Value, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: true), "[]"], services,
        CancellationToken.None))
      .Throws<JsonException>();
    await Assert.That(coordinator.BatchReconciles).IsEmpty().Because("nothing is reconciled until every payload reads");
  }

  [Test]
  public async Task Batch_AFailedBatchCall_FaultsTheBatchAsync() {
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { BatchFailure = new InvalidOperationException("store unreachable") };
    await using var services = _services(coordinator, ledger: null, logs: null);

    await Assert.That(async () => await RedeliveryReconciler.HandleDeferredBatchAsync(
        [_payload(TrackedGuid.New().Value, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: true)], services,
        CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Batch_ABundleWhoseConclusionFails_IsLogged_AndTheOthersStillConcludeAsync() {
    var (failing, healthy) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var stream = TrackedGuid.New().Value;
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      BatchAnswer = _ => [
        [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, 1, 1, OriginsFilled: 1)],
        [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, 1, 1, OriginsFilled: 1)],
      ],
    };
    var logs = new CapturingLoggerFactory();
    var ledger = new FailingForOriginLedger(failing);
    var services = new ServiceCollection()
      .AddScoped<IWorkCoordinator>(_ => coordinator)
      .AddSingleton<IIntegrityRepairLedger>(ledger)
      .AddSingleton<ILoggerFactory>(logs)
      .BuildServiceProvider();
    await using (services) {
      await RedeliveryReconciler.HandleDeferredBatchAsync(
        [_payload(failing, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: true),
         _payload(healthy, ORIGIN_NAME, TrackedGuid.New().Value, 2, allPresent: true)],
        services, CancellationToken.None);
    }

    await Assert.That(ledger.Answered).IsEquivalentTo([healthy])
      .Because("the second bundle is concluded although the first one's conclusion failed");
    await Assert.That(logs.Messages.Count(m => m.Contains(failing.ToString(), StringComparison.Ordinal)
        && m.Contains("failed", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1)
      .Because("the failed conclusion is logged, naming its origin");
  }

  [Test]
  public async Task Batch_CancellationDuringAConclusion_IsNotSwallowedAsync() {
    var origin = TrackedGuid.New().Value;
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      BatchAnswer = _ => [[new RedeliveredBucketPresence(TrackedGuid.New().Value, null, EVENT_TYPE, 1, 1, OriginsFilled: 1)]],
    };
    var services = new ServiceCollection()
      .AddScoped<IWorkCoordinator>(_ => coordinator)
      .AddSingleton<IIntegrityRepairLedger>(new FailingForOriginLedger(origin, new OperationCanceledException()))
      .BuildServiceProvider();
    await using (services) {
      await Assert.That(async () => await RedeliveryReconciler.HandleDeferredBatchAsync(
          [_payload(origin, ORIGIN_NAME, TrackedGuid.New().Value, 1, allPresent: true)], services, CancellationToken.None))
        .Throws<OperationCanceledException>();
    }
  }

  // ─── Fixture ────────────────────────────────────────────────────────────────────────────────────────────────────────

  private static ServiceProvider _services(
      InMemoryIntegrityDeliveryCoordinator coordinator, IntegrityRepairLedger? ledger, CapturingLoggerFactory? logs) {
    var services = new ServiceCollection().AddScoped<IWorkCoordinator>(_ => coordinator);
    if (ledger is not null) {
      services.AddSingleton(ledger);
    }
    if (logs is not null) {
      services.AddSingleton<ILoggerFactory>(logs);
    }
    return services.BuildServiceProvider();
  }

  /// <summary>A recorded reconcile in the form the reconcile writes it.</summary>
  private static string _payload(Guid origin, string? name, Guid eventId, long? sequence, bool? allPresent) {
    var nameJson = name is null ? "null" : $"\"{name}\"";
    var sequenceJson = sequence?.ToString(CultureInfo.InvariantCulture) ?? "null";
    var allPresentJson = allPresent switch {
      true => "true",
      false => "false",
      null => "null",
    };
    return $$"""{"originServiceId":"{{origin}}","originServiceName":{{nameJson}},"eventIds":["{{eventId}}"],"commitSequences":[{{sequenceJson}}],"allPresentAtCommit":{{allPresentJson}}}""";
  }

  /// <summary>A ledger whose answer for one origin fails; it records the origins it answered.</summary>
  private sealed class FailingForOriginLedger(Guid failingOrigin, Exception? failure = null) : IIntegrityRepairLedger {
    public List<Guid> Answered { get; } = [];

    public ValueTask<bool> TryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask<bool> TryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;

    public ValueTask MarkAnsweredAsync(
        IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default) {
      if (keys.Any(k => k.OriginServiceId == failingOrigin)) {
        return ValueTask.FromException(failure ?? new InvalidOperationException("ledger unreachable"));
      }
      Answered.AddRange(keys.Select(k => k.OriginServiceId).Distinct());
      return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>> MarkUnhealableBatchAsync(
        IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>>([]);
  }

  /// <summary>Collects every formatted log message from every logger it creates.</summary>
  private sealed class CapturingLoggerFactory : ILoggerFactory {
    public List<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => new Logger(Messages);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class Logger(List<string> messages) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (messages) {
          messages.Add(formatter(state, exception));
        }
      }
    }
  }
}
