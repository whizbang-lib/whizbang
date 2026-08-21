using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Stream-integrity Phase S: the consumed-type registry — first-boot registrations land as
/// Baseline (nothing existed to miss), later additions as Pending (an expansion), registration is
/// idempotent (never demotes an existing row), and only Pending rows transition to Requested.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/086_ConsumedTypeRegistry.sql</code-under-test>
[Category("Integration")]
[NotInParallel("ConsumedTypeRegistry")]
[Category("Shard3")]
public class ConsumedTypeRegistryTests : EFCoreTestBase {

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Register_BaselineThenExpansion_TracksStatusesAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    // First boot: the whole catalog registers as Baseline.
    await coordinator.RegisterConsumedTypesAsync(["Contracts.TypeA", "Contracts.TypeB"], asBaseline: true);
    var afterFirstBoot = await coordinator.GetConsumedTypeRegistrationsAsync();
    await Assert.That(afterFirstBoot.Count).IsEqualTo(2);
    await Assert.That(afterFirstBoot.All(r => r.Status == ConsumedTypeBackfillStatus.Baseline)).IsTrue()
      .Because("first-boot registration means nothing existed to miss — no backfill.");

    // A later boot adds a type: an EXPANSION registers as Pending. Re-registering existing types
    // must never demote them (idempotent ON CONFLICT DO NOTHING).
    await coordinator.RegisterConsumedTypesAsync(["Contracts.TypeA", "Contracts.TypeC"], asBaseline: false);
    var afterExpansion = (await coordinator.GetConsumedTypeRegistrationsAsync()).ToDictionary(r => r.EventType, r => r.Status);
    await Assert.That(afterExpansion["Contracts.TypeA"]).IsEqualTo(ConsumedTypeBackfillStatus.Baseline)
      .Because("an already-registered type is untouched — registration never demotes or re-pends.");
    await Assert.That(afterExpansion["Contracts.TypeC"]).IsEqualTo(ConsumedTypeBackfillStatus.Pending)
      .Because("a type appearing on a later boot is an expansion — history exists this service never received.");

    // Requesting the backfill transitions ONLY Pending rows.
    await coordinator.MarkConsumedTypeBackfillRequestedAsync(["Contracts.TypeA", "Contracts.TypeC"]);
    var afterRequest = (await coordinator.GetConsumedTypeRegistrationsAsync()).ToDictionary(r => r.EventType, r => r.Status);
    await Assert.That(afterRequest["Contracts.TypeC"]).IsEqualTo(ConsumedTypeBackfillStatus.Requested);
    await Assert.That(afterRequest["Contracts.TypeA"]).IsEqualTo(ConsumedTypeBackfillStatus.Baseline)
      .Because("Baseline rows never backfill — only Pending transitions to Requested.");
  }
}
