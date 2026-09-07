#pragma warning disable CA1707, CA1034

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for the folds in <see cref="PerEventApplyHooks"/> that the existing
/// <c>PerEventApplyHooksTests</c> never reach: the where/RemoveSetter no-op branch of
/// <c>Resolve</c>'s op switch, all three value shapes <c>_asDateTimeOffset</c> can see from a hook's
/// <c>SetColumn(updated_at, ...)</c> call (null, a bare <c>DateTime</c>, and an unsupported type),
/// and the boxing-<c>Convert</c> unwrap inside <c>_compileSetter</c> (both the successful unwrap and
/// the case where, even after unwrapping, the selector still isn't a property). Pure logic -- no
/// database, no process-wide state (uses the explicit-registry <c>Resolve</c> overload throughout,
/// so <see cref="PerEventApplyHooks.Registry"/> is never touched).
/// </summary>
[Category("Shard1")]
public class PerEventApplyHooksCoverageTests {

  private sealed class _model {
    public int Count { get; set; }
  }

  private sealed class _perEventHook<TMarker>(Action<IApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : IApplyHook<TMarker> {
    public void Configure(IApplyHookBuilder<TMarker> b, ApplyHookContext c) => body(b, c);
  }

  private static ApplyHookContext _ctx(DateTimeOffset? stamp = null) => new() {
    ModelType = typeof(_model),
    ApplyTimestamp = stamp ?? DateTimeOffset.UnixEpoch,
  };

  // ---- Resolve: RemoveSetterOp folds to nothing on the per-event path ------------------------

  [Test]
  public async Task Resolve_WithARemoveSetterFromAPerEventHook_IsANoOpAsync() {
    // RemoveSetter exists for the collective path (dropping a setter an earlier stage already added
    // to the batched UPDATE). A per-event apply has no setter list to remove from; if this default
    // branch regressed into throwing or into adding a phantom setter, a hook author reusing the same
    // builder body across both paths (a documented, intended pattern) would either crash the
    // per-event write or corrupt the row with a mutation nobody asked for.
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<_model>(new _perEventHook<_model>((b, _) => b.RemoveSetter(m => m.Count)));

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());

    await Assert.That(plan.ModelFieldSetters).IsEmpty()
      .Because("RemoveSetter is collective-only cleanup; per-event has no setter list to drop from");
    await Assert.That(plan.BumpVersion).IsTrue()
      .Because("the default whizbang.timestamps hook still runs alongside the no-op RemoveSetter");
  }

  // ---- Resolve: _asDateTimeOffset's three value shapes ----------------------------------------

  [Test]
  public async Task Resolve_WithSetColumnUpdatedAtNull_YieldsNullUpdatedAtAsync() {
    // A hook that explicitly declines to stamp updated_at (as opposed to one that never mentions it
    // at all) must not crash the fold and must not accidentally invent a timestamp -- a wrong stamp
    // here is a silently corrupted "last changed" time that outlives the request that caused it.
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new _perEventHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, null)),
        key: WhizbangApplyHookKeys.TIMESTAMPS);

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());

    await Assert.That(plan.UpdatedAt).IsNull()
      .Because("an explicit null must pass through as null, not get coerced into a fabricated timestamp");
  }

  [Test]
  public async Task Resolve_WithSetColumnUpdatedAtAsAnUnspecifiedKindDateTime_CoercesToUtcAsync() {
    // updated_at is stored as a UTC timestamptz. A hook handing over a bare DateTime with no Kind
    // (built from raw component parts rather than DateTimeOffset.UtcNow) must still land as UTC --
    // if this coercion regressed, an unspecified-kind value would be reinterpreted at whatever the
    // ambient time zone happens to be downstream, silently shifting that row's recency ordering.
    var naive = new DateTime(2030, 5, 6, 7, 8, 9, DateTimeKind.Unspecified);
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new _perEventHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, naive)),
        key: WhizbangApplyHookKeys.TIMESTAMPS);

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());

    await Assert.That(plan.UpdatedAt).IsEqualTo(new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc)))
      .Because("an unspecified-kind DateTime must be treated as UTC, not silently misinterpreted");
  }

  [Test]
  public async Task Resolve_WithSetColumnUpdatedAtAsAnUnsupportedType_ThrowsAsync() {
    // updated_at can only ever be a point in time. A hook that hands over some other type (a typo, a
    // wrong variable) must fail loudly at apply time rather than get silently coerced -- a bad
    // timestamp reaching the row is far worse than a startup-time exception pointing at the hook.
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<object>(new _perEventHook<object>((b, _) => b.SetColumn(ApplyHookColumns.UPDATED_AT, 12345)),
        key: WhizbangApplyHookKeys.TIMESTAMPS);

    await Assert.That(() => PerEventApplyHooks.Resolve(registry, _ctx()))
      .Throws<NotSupportedException>()
      .Because("an unsupported updated_at value type must not be silently ignored or coerced");

    await Assert.That(() => PerEventApplyHooks.Resolve(registry, _ctx()))
      .Throws<NotSupportedException>()
      .WithMessageContaining("Int32");
  }

  // ---- _compileSetter: unwrapping a boxing Convert around a real property ---------------------

  [Test]
  public async Task ApplyModelSetters_WithASetPropertySelectorBoxedThroughObject_StillMutatesAsync() {
    // A hook author writing one generic helper that sets several differently-typed properties
    // (SetProperty<object>(selector, value)) is a legitimate, unremarkable pattern -- the compiler
    // inserts a boxing Convert around the property access. If the compiled setter stopped unwrapping
    // that Convert, the hook would silently fail to compile a setter and the property would never be
    // mutated -- the row persists with stale data and no error anywhere.
    var registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
      .Register<_model>(new _perEventHook<_model>((b, _) => b.SetProperty<object>(m => m.Count, 42)));

    var plan = PerEventApplyHooks.Resolve(registry, _ctx());
    var model = new _model();
    PerEventApplyHooks.ApplyModelSetters(model, plan.ModelFieldSetters);

    await Assert.That(model.Count).IsEqualTo(42)
      .Because("the boxed selector must still resolve to the real Count property and mutate it");
  }

  [Test]
  public async Task ApplyModelSetters_WithASelectorThatIsNeverAPropertyEvenAfterUnwrapping_ThrowsAsync() {
    // ApplyHookSelector already rejects a non-property selector when a hook calls SetProperty
    // through the builder, so this exact path is unreachable from the public per-event DSL. It is
    // defense-in-depth for a SetPropertyOp constructed directly (bypassing the builder) -- if this
    // check silently no-opped instead of throwing, a malformed op would compile no setter and the
    // property it claimed to set would quietly stay unchanged.
    var param = Expression.Parameter(typeof(_model), "m");
    var body = Expression.Convert(Expression.Constant(1), typeof(object));
    var badSelector = Expression.Lambda<Func<_model, object>>(body, param);
    var badOp = new SetPropertyOp(badSelector, "Bogus", 42, typeof(int));

    await Assert.That(() => PerEventApplyHooks.ApplyModelSetters(new _model(), [badOp]))
      .Throws<NotSupportedException>()
      .Because("a selector that isn't a top-level property access must fail loudly rather than "
             + "silently compile no setter");

    await Assert.That(() => PerEventApplyHooks.ApplyModelSetters(new _model(), [badOp]))
      .Throws<NotSupportedException>()
      .WithMessageContaining("Bogus");
  }
}
