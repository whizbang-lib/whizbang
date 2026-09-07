#pragma warning disable CA1707

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Coverage for <see cref="CollectiveApplyHookPlanner.Resolve{TModel}"/>'s switch default arm.
/// <see cref="ApplyHookOp"/> is not sealed, but the ONLY subtype reachable through the public
/// registration surface that the switch does not name is <see cref="SuppressActivityOp"/> — every
/// <see cref="ICollectiveApplyHookBuilder{TMarker}"/> inherits <c>SuppressActivity()</c> from
/// <see cref="IApplyHookBuilder{TMarker}"/> (a per-event-path verb), so a collective hook CAN call
/// it even though <see cref="CollectiveApplyHookPlan{TModel}"/> has no field to carry it — the fold
/// silently drops it. Pure in-memory fold logic — no database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlanner.cs</code-under-test>
[Category("Shard1")]
public class CollectiveApplyHookPlannerCoverageTests {

  // A collective (SQL-compile) hook that calls SuppressActivity() — reasonable to try, since the
  // verb is right there on the builder it was handed — silently loses that declaration on this
  // path: CollectiveApplyHookPlan has no slot for it, so updated_at business-time suppression
  // never reaches the compiled UPDATE. A maintenance/backfill process registering a COLLECTIVE
  // hook to keep its writes from looking like business activity would see updated_at (and
  // recency ordering, and retention) advance anyway, with no error or warning telling it why.
  [Test]
  public async Task Resolve_HookCallsSuppressActivity_IsSilentlyDroppedFromThePlanAsync() {
    var registry = new CollectiveApplyHookRegistry();
    registry.Register<_model>(new _suppressActivityHook());
    var context = new ApplyHookContext {
      ModelType = typeof(_model),
      ApplyTimestamp = DateTimeOffset.UtcNow,
    };

    var plan = CollectiveApplyHookPlanner.Resolve<_model>(registry, context);

    await Assert.That(plan.StoreColumns).IsEmpty()
      .Because("SuppressActivityOp has no representation in CollectiveStoreColumn — the collective "
             + "fold's switch has no case for it and must fall through its default arm rather than "
             + "mistakenly emitting a store-column assignment");
    await Assert.That(plan.BumpVersion).IsFalse()
      .Because("SuppressActivity() must not be mistaken for BumpVersionOp — the two are unrelated "
             + "verbs that happen to both be no-representation cases from this fold's point of view");
  }

  private sealed class _model {
    public string Name { get; set; } = string.Empty;
  }

  private sealed class _suppressActivityHook : ICollectiveApplyHook<_model> {
    public void Configure(ICollectiveApplyHookBuilder<_model> builder, ApplyHookContext context)
      => builder.SuppressActivity();
  }
}
