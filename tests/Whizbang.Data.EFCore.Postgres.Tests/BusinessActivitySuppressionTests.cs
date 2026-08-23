using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks that an apply hook can declare an event NOT to be business activity, so it writes the row
/// without advancing business time.
/// </summary>
/// <remarks>
/// <para>
/// "When a user or business process acted on this record" is narrower than "when any event touched
/// this row". Integrity repairs, system backfills, reclassification passes and maintenance-generated
/// events all write rows without representing domain activity. If they advanced business time they
/// would extend retention windows and lift records to the top of recency ordering — the same
/// conflation the two-axis split removes, one level down.
/// </para>
/// <para>
/// The default is opt-out: every event counts as activity unless a hook says otherwise. Forgetting
/// to declare keeps a record alive and visible, which fails safe; an opt-in default would silently
/// expire records nobody remembered to annotate.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
[Category("Shard3")]
public class BusinessActivitySuppressionTests {
  private sealed record SuppressionModel {
    public string Name { get; init; } = "";
  }

  private sealed class _suppressHook : IApplyHook<object> {
    public void Configure(IApplyHookBuilder<object> builder, ApplyHookContext context) =>
      builder.SuppressActivity();
  }

  [Test]
  public async Task DefaultHook_TreatsEveryEventAsActivityAsync() {
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults();
    var stamp = new DateTimeOffset(2021, 4, 5, 6, 7, 8, TimeSpan.Zero);

    var plan = PerEventApplyHooks.Resolve(registry, new ApplyHookContext {
      ModelType = typeof(SuppressionModel),
      Scope = new Core.Lenses.PerspectiveScope(),
      ApplyTimestamp = stamp,
    });

    await Assert.That(plan.SuppressActivity).IsFalse()
      .Because("opt-out is the safe default — an unannotated event counts as activity, keeping the "
        + "record alive rather than silently expiring it");
    await Assert.That(plan.UpdatedAt).IsEqualTo(stamp)
      .Because("the default hook stamps business time, which is now the applied event's own timestamp");
  }

  [Test]
  public async Task SuppressActivityHook_LeavesBusinessTimeUnstampedAsync() {
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new _suppressHook(), key: "test.not-activity");

    var plan = PerEventApplyHooks.Resolve(registry, new ApplyHookContext {
      ModelType = typeof(SuppressionModel),
      Scope = new Core.Lenses.PerspectiveScope(),
      ApplyTimestamp = new DateTimeOffset(2021, 4, 5, 6, 7, 8, TimeSpan.Zero),
    });

    await Assert.That(plan.SuppressActivity).IsTrue()
      .Because("a hook declaring the event non-activity must reach the write path, so the upsert can "
        + "preserve the row's existing business time instead of advancing it");
  }
}
