// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The coordinator-backed ledger's phantom heal: one round trip that forgets every row of the proven types when the
/// store supports it, and, when it does not, cannot be reached or fails, the type lanes alone through the batch heal, so
/// a proven type's type lane still clears as #2207 made it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CoordinatorIntegrityRepairLedger.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
[Category("Shard4")]
public class CoordinatorIntegrityRepairLedgerTypeHealTests {
  private sealed class TypeHealCoordinator : IWorkCoordinator {
    public IReadOnlyList<double>? TypesAnswer { get; set; }
    public Exception? TypesThrows { get; set; }
    public List<IntegrityRepairLedger.DivergenceKey> TypesAsked { get; } = [];
    public List<IntegrityRepairLedger.DivergenceKey> BatchHealed { get; } = [];

    public Task<IReadOnlyList<double>?> IntegrityMarkTypesHealedWithAgesAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> typeKeys, CancellationToken cancellationToken = default) {
      TypesAsked.AddRange(typeKeys);
      return TypesThrows is { } ex ? Task.FromException<IReadOnlyList<double>?>(ex) : Task.FromResult(TypesAnswer);
    }

    public Task<IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken cancellationToken = default) {
      BatchHealed.AddRange(keys);
      return Task.FromResult<IReadOnlyList<double>?>([.. keys.Select(_ => 1d)]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private static readonly double[] _storeAges = [3d, 4d, 5d];

  private static IIntegrityRepairLedger _ledger(IWorkCoordinator? coordinator) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    return new CoordinatorIntegrityRepairLedger(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
  }

  private static IntegrityRepairLedger.DivergenceKey _streamKey() =>
    new(TrackedGuid.New().Value, "tenant-a", "Contracts.TypeX", TrackedGuid.New().Value);

  [Test]
  public async Task MarkTypesHealed_WhenTheStoreSupportsIt_ForgetsEveryRowOfTheTypesInOneCallAsync() {
    var coordinator = new TypeHealCoordinator { TypesAnswer = _storeAges };
    var key = _streamKey();

    var ages = await _ledger(coordinator).MarkTypesHealedWithAgesAsync([key]);

    await Assert.That(ages).IsEquivalentTo(_storeAges);
    await Assert.That(coordinator.TypesAsked).IsEquivalentTo([key]);
    await Assert.That(coordinator.BatchHealed).IsEmpty();
  }

  [Test]
  public async Task MarkTypesHealed_WhenTheStoreCannot_ForgetsTheTypeLanesThroughTheBatchHealAsync() {
    var coordinator = new TypeHealCoordinator { TypesAnswer = null };
    var key = _streamKey();

    var ages = await _ledger(coordinator).MarkTypesHealedWithAgesAsync([key]);

    await Assert.That(coordinator.BatchHealed).IsEquivalentTo([key with { StreamId = Guid.Empty }])
      .Because("the type lane of a proven type still clears, as #2207 made it");
    await Assert.That(ages.Count).IsEqualTo(1);
  }

  [Test]
  public async Task MarkTypesHealed_WhenTheCallFails_ForgetsTheTypeLanesThroughTheBatchHealAsync() {
    var coordinator = new TypeHealCoordinator { TypesThrows = new InvalidOperationException("store down") };
    var key = _streamKey();

    _ = await _ledger(coordinator).MarkTypesHealedWithAgesAsync([key]);

    await Assert.That(coordinator.BatchHealed).IsEquivalentTo([key with { StreamId = Guid.Empty }]);
  }

  [Test]
  public async Task MarkTypesHealed_WhenCanceled_PropagatesAsync() {
    var coordinator = new TypeHealCoordinator { TypesThrows = new OperationCanceledException() };

    await Assert.That(async () => await _ledger(coordinator).MarkTypesHealedWithAgesAsync([_streamKey()]))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task MarkTypesHealed_WithNoCoordinator_ForgetsTheTypeLanesTheBatchWayAsync() {
    var ages = await _ledger(null).MarkTypesHealedWithAgesAsync([_streamKey()]);

    await Assert.That(ages).IsEmpty().Because("with no store the batch heal measures nothing");
  }

  [Test]
  public async Task MarkTypesHealed_WithNoKeys_AsksNothingAsync() {
    var coordinator = new TypeHealCoordinator { TypesAnswer = [1d] };

    var ages = await _ledger(coordinator).MarkTypesHealedWithAgesAsync([]);

    await Assert.That(ages).IsEmpty();
    await Assert.That(coordinator.TypesAsked).IsEmpty();
  }
}
