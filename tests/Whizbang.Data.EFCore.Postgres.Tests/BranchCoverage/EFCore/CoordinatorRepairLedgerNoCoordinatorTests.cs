// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="CoordinatorIntegrityRepairLedger"/>'s batch paths when the scope
/// resolves no <see cref="IWorkCoordinator"/>: the batch is skipped and each key falls back to the
/// single-key semantics, reports failing open and repairs failing closed. Runs in the same shard as
/// the batch-supported tests so both arms of the coordinator check are measured together.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CoordinatorIntegrityRepairLedger.cs</code-under-test>
[Category("Shard4")]
public class CoordinatorRepairLedgerNoCoordinatorTests {

  [Test]
  public async Task Batches_WithoutACoordinator_FallBackToSingleKeySemanticsAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var ledger = new CoordinatorIntegrityRepairLedger(sp.GetRequiredService<IServiceScopeFactory>());
    var key = new IntegrityRepairLedger.DivergenceKey(Guid.NewGuid(), "tenant-a", "Contracts.TypeX", Guid.NewGuid());

    var reports = await ledger.TryBeginReportBatchAsync(
      [new IntegrityReportObservation(key, 1, 2, 3, 4)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    var repairs = await ledger.TryBeginRepairBatchAsync(
      [key], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), maxAttempts: 3, maxGrants: 1);

    await Assert.That(reports).IsEquivalentTo([true])
      .Because("with no store a report is still allowed, as before the ledger existed");
    await Assert.That(repairs).IsEquivalentTo([false])
      .Because("with no store a repair is never licensed against real data");
  }
}
