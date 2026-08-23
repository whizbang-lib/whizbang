#pragma warning disable CA1707, CA1034

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Fast unit coverage for the per-event apply-hook interpreter (<see cref="PerEventApplyHooks"/>): folding the
/// resolved ops into a <see cref="PerEventApplyHookPlan"/> (updated_at, version bump, model-field setters) and
/// the compiled in-place model-field setter. Pure logic — no database. Uses the explicit-registry
/// <see cref="PerEventApplyHooks.Resolve(ApplyHookRegistry, ApplyHookContext)"/> overload so the process-wide
/// static is never touched.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
[Category("Shard1")]
public class PerEventApplyHooksTests {

  private sealed class _model {
    public string Status { get; set; } = "";
    public int Count { get; set; }
  }

  private sealed class _unrelatedModel { }

  private sealed class _perEventHook<TMarker>(Action<IApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : IApplyHook<TMarker> {
    public void Configure(IApplyHookBuilder<TMarker> b, ApplyHookContext c) => body(b, c);
  }

  private static ApplyHookContext _ctx(DateTimeOffset? stamp = null) => new() {
    ModelType = typeof(_model),
    ApplyTimestamp = stamp ?? DateTimeOffset.UnixEpoch,
  };

  [Test]
  public async Task DefaultTimestampsHook_YieldsUpdatedAtAndBumpAsync() {
    var stamp = new DateTimeOffset(2032, 6, 7, 8, 9, 10, TimeSpan.Zero);
    var plan = PerEventApplyHooks.Resolve(WhizbangApplyHooks.CreatePerEventWithDefaults(), _ctx(stamp));

    await Assert.That(plan.UpdatedAt).IsEqualTo(stamp)
      .Because("The default whizbang.timestamps hook stamps updated_at = ApplyTimestamp.");
    await Assert.That(plan.BumpVersion).IsTrue();
    await Assert.That(plan.ModelFieldSetters).IsEmpty();
  }

  [Test]
  public async Task OverrideTimestamps_SetsUpdatedAt_WithoutBumpAsync() {
    var sentinel = new DateTimeOffset(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new _perEventHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, sentinel)),
        key: WhizbangApplyHookKeys.TIMESTAMPS);

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());

    await Assert.That(plan.UpdatedAt).IsEqualTo(sentinel);
    await Assert.That(plan.BumpVersion).IsFalse()
      .Because("The override sets updated_at but does not BumpVersion — the default bump was replaced.");
  }

  [Test]
  public async Task SetProperty_IsCarried_AndApplyModelSettersMutatesTheObjectAsync() {
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<_model>(new _perEventHook<_model>((b, _) => b.SetProperty(m => m.Status, "Hooked")))
      .Register<_model>(new _perEventHook<_model>((b, _) => b.SetProperty(m => m.Count, 42)));

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());
    await Assert.That(plan.ModelFieldSetters.Count).IsEqualTo(2);

    var model = new _model { Status = "Original", Count = 1 };
    PerEventApplyHooks.ApplyModelSetters(model, plan.ModelFieldSetters);

    await Assert.That(model.Status).IsEqualTo("Hooked");
    await Assert.That(model.Count).IsEqualTo(42);
  }

  [Test]
  public async Task UnsupportedStoreColumn_ThrowsAsync() {
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<_model>(new _perEventHook<_model>((b, _) => b.SetColumn("audit_col", "x")));

    await Assert.That(() => PerEventApplyHooks.Resolve(registry, _ctx()))
      .Throws<NotSupportedException>()
      .Because("Per-event hooks can only SetColumn updated_at; arbitrary physical columns are collective-only.");
  }

  [Test]
  public async Task NonMatchingMarkerHook_DoesNotContributeSettersAsync() {
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<_unrelatedModel>(new _perEventHook<_unrelatedModel>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, DateTimeOffset.UnixEpoch)));

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());

    await Assert.That(plan.ModelFieldSetters).IsEmpty()
      .Because("A hook gated on an unrelated model must not contribute setters for _model.");
    await Assert.That(plan.BumpVersion).IsTrue()
      .Because("The default hook still applies to _model.");
  }
}
