#pragma warning disable CA1707, CA1034

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Core.Tests.Perspectives.Hooks;

/// <summary>
/// Unit coverage for the shared apply-hook core: marker-gated matching (concrete / base / interface), matching
/// hooks fire in registration order, keyed override-in-place, the documented default key, and that the
/// per-apply <see cref="ApplyHookContext"/> reaches the hook. Pure logic — no database, no driver. Both the
/// collective and the per-event registry share <see cref="MarkerHookRegistryBase"/>, so the collective registry
/// is covered thoroughly and the per-event registry covers the shared mechanics plus its own entry point.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public class ApplyHookRegistryTests {

  private interface IAuditable {
    string Status { get; }
  }

  private class _baseModel {
    public string Status { get; set; } = "Draft";
  }

  private sealed class _job : _baseModel, IAuditable {
    public string Title { get; set; } = "";
  }

  private sealed class _unrelated { }

  private sealed class _collectiveHook<TMarker>(Action<ICollectiveApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : ICollectiveApplyHook<TMarker> {
    public void Configure(ICollectiveApplyHookBuilder<TMarker> builder, ApplyHookContext context) => body(builder, context);
  }

  private sealed class _perEventHook<TMarker>(Action<IApplyHookBuilder<TMarker>, ApplyHookContext> body)
      : IApplyHook<TMarker> {
    public void Configure(IApplyHookBuilder<TMarker> builder, ApplyHookContext context) => body(builder, context);
  }

  private static ApplyHookContext _ctx(Type model, DateTimeOffset? ts = null) => new() {
    ModelType = model,
    ApplyTimestamp = ts ?? DateTimeOffset.UnixEpoch,
  };

  private static List<ApplyHookOp> _run(MarkerHookRegistryBase registry, Type model, DateTimeOffset? ts = null) {
    var ctx = _ctx(model, ts);
    return registry.ResolveFor(model).SelectMany(p => p(ctx)).ToList();
  }

  // Pipe-joined SetColumn values, preserving order — lets a single IsEqualTo assert both the set AND its order
  // without a constant-array argument (CA1861).
  private static string _columns(IEnumerable<ApplyHookOp> ops) =>
    string.Join("|", ops.OfType<SetColumnOp>().Select(o => (string)o.Value!));

  // ── Marker-gated matching ─────────────────────────────────────────────

  [Test]
  public async Task ConcreteMarker_FiresForThatModelAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "concrete")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("concrete");
  }

  [Test]
  public async Task BaseClassMarker_FiresForDerivedModelAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_baseModel>(new _collectiveHook<_baseModel>((b, _) => b.SetColumn("m", "base")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("base")
      .Because("A hook gated on a base class matches any model assignable to it.");
  }

  [Test]
  public async Task InterfaceMarker_FiresForImplementingModelAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<IAuditable>(new _collectiveHook<IAuditable>((b, _) => b.SetColumn("m", "iface")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("iface")
      .Because("A hook gated on an interface matches any model that implements it.");
  }

  [Test]
  public async Task ObjectMarker_FiresForEveryModelAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<object>(new _collectiveHook<object>((b, _) => b.SetColumn("m", "all")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("all");
    await Assert.That(_columns(_run(reg, typeof(_unrelated)))).IsEqualTo("all")
      .Because("object is assignable from every model — the default-hook marker.");
  }

  [Test]
  public async Task NonAssignableMarker_IsSkippedAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "job")));

    await Assert.That(_run(reg, typeof(_unrelated))).IsEmpty()
      .Because("A _job-gated hook must not fire for an unrelated model.");
  }

  // ── Accumulation + registration order ─────────────────────────────────

  [Test]
  public async Task MultipleRegistrations_SameMarker_AllFireInRegistrationOrderAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "first")));
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "second")));
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "third")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("first|second|third");
  }

  [Test]
  public async Task DifferentMarkers_MatchingModel_FireInRegistrationOrderAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<IAuditable>(new _collectiveHook<IAuditable>((b, _) => b.SetColumn("m", "iface")));
    reg.Register<_baseModel>(new _collectiveHook<_baseModel>((b, _) => b.SetColumn("m", "base")));
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "concrete")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("iface|base|concrete")
      .Because("All matching markers fire, ordered by registration — not by marker specificity.");
  }

  // ── Keyed override-in-place ───────────────────────────────────────────

  [Test]
  public async Task KeyedOverride_ReplacesInPlace_KeepingOrderPositionAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "A")), key: "k");
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "B")));       // unkeyed, after
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "C")), key: "k"); // replaces slot 0

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("C|B")
      .Because("Re-registering an existing key replaces the hook at that slot, preserving its order position.");
  }

  [Test]
  public async Task UnkeyedRegistration_AlwaysAppendsAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "A")), key: "k");
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "B")));
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "C")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("A|B|C");
  }

  [Test]
  public async Task DefaultKey_IsOverriddenByReRegisteringSameKeyAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<object>(new TimestampsApplyHook(), key: WhizbangApplyHookKeys.TIMESTAMPS);
    // Developer overrides the default stamping with their own hook under the same documented key.
    reg.Register<object>(new _collectiveHook<object>((b, _) => b.SetColumn("audit", "custom")), key: WhizbangApplyHookKeys.TIMESTAMPS);

    var ops = _run(reg, typeof(_job));
    await Assert.That(ops.OfType<BumpVersionOp>()).IsEmpty()
      .Because("Overriding whizbang.timestamps replaces the default stamp+bump entirely.");
    await Assert.That(_columns(ops)).IsEqualTo("custom");
  }

  // ── Context propagation ───────────────────────────────────────────────

  [Test]
  public async Task ApplyTimestamp_ReachesTheHookAsync() {
    var stamp = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<object>(new TimestampsApplyHook(), key: WhizbangApplyHookKeys.TIMESTAMPS);

    var ops = _run(reg, typeof(_job), stamp);

    var updatedAt = ops.OfType<SetColumnOp>().Single(o => o.Column == ApplyHookColumns.UPDATED_AT);
    await Assert.That((DateTimeOffset)updatedAt.Value!).IsEqualTo(stamp);
    await Assert.That(ops.OfType<BumpVersionOp>().Count()).IsEqualTo(1)
      .Because("The default hook stamps updated_at and bumps version.");
  }

  // ── Verb recording ────────────────────────────────────────────────────

  [Test]
  public async Task SetProperty_RecordsNameValueAndTypeAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetProperty(j => j.Status, "Archived")));

    var op = _run(reg, typeof(_job)).OfType<SetPropertyOp>().Single();
    await Assert.That(op.PropertyName).IsEqualTo("Status");
    await Assert.That(op.Value).IsEqualTo("Archived");
    await Assert.That(op.PropertyType).IsEqualTo(typeof(string));
  }

  [Test]
  public async Task RemoveSetter_RecordsPropertyNameAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.RemoveSetter(j => j.Title)));

    var op = _run(reg, typeof(_job)).OfType<RemoveSetterOp>().Single();
    await Assert.That(op.PropertyName).IsEqualTo("Title");
  }

  [Test]
  public async Task CollectiveBuilder_RecordsAndWhereAndReplaceWhereAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => {
      b.AndWhere(j => j.Status == "Draft");
      b.ReplaceWhere(j => j.Title == "x");
    }));

    var ops = _run(reg, typeof(_job));
    await Assert.That(ops.OfType<AndWhereOp>().Count()).IsEqualTo(1);
    await Assert.That(ops.OfType<ReplaceWhereOp>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task BumpVersion_IsRecordedAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.BumpVersion()));

    await Assert.That(_run(reg, typeof(_job)).OfType<BumpVersionOp>().Count()).IsEqualTo(1);
  }

  // ── Late registration invalidates the per-model cache ─────────────────

  [Test]
  public async Task ResolveAfterFurtherRegistration_SeesNewHooksAsync() {
    var reg = new CollectiveApplyHookRegistry();
    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "one")));
    _ = _run(reg, typeof(_job)); // prime the per-model cache

    reg.Register<_job>(new _collectiveHook<_job>((b, _) => b.SetColumn("m", "two")));

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("one|two")
      .Because("A registration after a resolve must invalidate the memoized per-model list.");
  }

  // ── Per-event registry: shared mechanics + its own entry point ────────

  [Test]
  public async Task PerEventRegistry_Marker_Order_And_KeyedOverrideAsync() {
    var reg = new ApplyHookRegistry();
    reg.Register<_baseModel>(new _perEventHook<_baseModel>((b, _) => b.SetColumn("m", "A")), key: "k");
    reg.Register<_job>(new _perEventHook<_job>((b, _) => b.SetColumn("m", "B")));
    reg.Register<_baseModel>(new _perEventHook<_baseModel>((b, _) => b.SetColumn("m", "C")), key: "k");

    await Assert.That(_columns(_run(reg, typeof(_job)))).IsEqualTo("C|B")
      .Because("The per-event registry shares the base marker-match + keyed-override mechanics.");
    await Assert.That(_run(reg, typeof(_unrelated))).IsEmpty();
  }

  [Test]
  public async Task PerEventRegistry_DefaultTimestampsHook_StampsAndBumpsAsync() {
    var stamp = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var reg = new ApplyHookRegistry();
    reg.Register<object>(new TimestampsApplyHook(), key: WhizbangApplyHookKeys.TIMESTAMPS);

    var ops = _run(reg, typeof(_job), stamp);
    var updatedAt = ops.OfType<SetColumnOp>().Single(o => o.Column == ApplyHookColumns.UPDATED_AT);
    await Assert.That((DateTimeOffset)updatedAt.Value!).IsEqualTo(stamp);
    await Assert.That(ops.OfType<BumpVersionOp>().Count()).IsEqualTo(1);
  }
}
