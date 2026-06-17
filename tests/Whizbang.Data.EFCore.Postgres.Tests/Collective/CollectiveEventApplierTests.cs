#pragma warning disable CA1707

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Locks the validation + composition contract of
/// <see cref="CollectiveEventApplier{TModel}"/>. The tests cover the
/// defensive guards that fire BEFORE the SQL UPDATE so misconfigured
/// dispatch surfaces as a clear ArgumentException rather than silently
/// corrupting the projection.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveEventApplierTests {

  // ── Type-mismatch guards ───────────────────────────────────────────────

  [Test]
  public async Task ApplyAsync_EventTypeMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeB(new _tenantScope("t"), []);
    var resolver = new _stubResolver("tenant");
    using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), evt, resolver, ctx, Guid.NewGuid()))
      .ThrowsExactly<ArgumentException>()
      .Because("Entry registered for _typeA but dispatched a _typeB — that's a registry routing bug, not a domain condition.");
  }

  [Test]
  public async Task ApplyAsync_ModelTypeMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<_typeA>(typeof(_otherModel), "Apply"); // mismatched TModel
    var evt = new _typeA(new _tenantScope("t"), []);
    var resolver = new _stubResolver("tenant");
    using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), evt, resolver, ctx, Guid.NewGuid()))
      .ThrowsExactly<ArgumentException>()
      .Because("Dispatching to CollectiveEventApplier<_jobModel> with an entry whose ModelType is _otherModel means the type-fanout in the upstream dispatcher is wrong.");
  }

  [Test]
  public async Task ApplyAsync_ScopeKindMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []);
    var resolver = new _stubResolver("workspace"); // wrong kind
    using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), evt, resolver, ctx, Guid.NewGuid()))
      .ThrowsExactly<ArgumentException>()
      .Because("DI should have dispatched the 'tenant' event to the tenant resolver — a mismatched resolver means the registry lookup is wrong.");
  }

  // ── Null-guard contract ────────────────────────────────────────────────

  [Test]
  public async Task ApplyAsync_NullEntry_ThrowsArgumentNullAsync() {
    var evt = new _typeA(new _tenantScope("t"), []);
    using var ctx = _newCtx();
    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        null!, new _handler(), evt, new _stubResolver("tenant"), ctx, Guid.NewGuid()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ApplyAsync_NullHandlerInstance_ThrowsArgumentNullAsync() {
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []);
    using var ctx = _newCtx();
    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, null!, evt, new _stubResolver("tenant"), ctx, Guid.NewGuid()))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── EnterContext is called before Invoker, restored after ──────────────

  [Test]
  public async Task ApplyAsync_EmptyMatchedSet_ShortCircuitsTo_ZeroAffectedAsync() {
    // Slice 6's adapter short-circuits an empty matched-set to
    // Task.FromResult(0). The applier exercises this end-to-end without
    // hitting the SQL provider (no Postgres needed).
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []); // empty set
    var resolver = new _stubResolver("tenant");
    using var ctx = _newCtx();

    var prior = ScopeContextAccessor.CurrentContext;
    try {
      var affected = await CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), evt, resolver, ctx, Guid.NewGuid());

      await Assert.That(affected).IsEqualTo(0)
        .Because("Empty matched set is a well-formed no-op; the adapter returns 0 without issuing SQL.");
      await Assert.That(resolver.EnterCount).IsEqualTo(1)
        .Because("Resolver.EnterContext must run BEFORE the handler is invoked so ambient scope is in place if the handler reads it.");
      await Assert.That(resolver.ExitCount).IsEqualTo(1)
        .Because("Disposing the EnterContext result must run AFTER apply, even on the no-op path.");
      await Assert.That(ScopeContextAccessor.CurrentContext).IsEqualTo(prior)
        .Because("Ambient context must be restored to its prior value once the apply finishes.");
    } finally {
      ScopeContextAccessor.CurrentContext = prior;
    }
  }

  [Test]
  public async Task ApplyAsync_HandlerInvokedOnce_ReceivesEventAsync() {
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []);
    var handler = new _handler();

    await CollectiveEventApplier<_jobModel>.ApplyAsync(
      entry, handler, evt, new _stubResolver("tenant"), _newCtx(), Guid.NewGuid());

    await Assert.That(handler.InvocationCount).IsEqualTo(1)
      .Because("Applier MUST call the entry's Invoker exactly once with the supplied handler + event.");
    await Assert.That(handler.LastEvent).IsSameReferenceAs(evt);
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
  }
  private sealed class _otherModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed record _tenantScope(string TenantId) : ICollectiveScope {
    public string ScopeKind => "tenant";
  }

  private sealed record _typeA(ICollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;
  private sealed record _typeB(ICollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed class _handler {
    public int InvocationCount { get; private set; }
    public ICollectiveEvent? LastEvent { get; private set; }
    public ICollectiveSpec<_jobModel> Apply(_typeA e) {
      InvocationCount++;
      LastEvent = e;
      return new _spec();
    }
  }

  private sealed class _spec : ICollectiveSpec<_jobModel> {
    public Expression<Action<ICollectiveSetters<_jobModel>>> Setters { get; } =
      s => s.SetProperty(j => j.Status, "X");
  }

  private sealed class _stubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public int EnterCount { get; private set; }
    public int ExitCount { get; private set; }
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;
    public IDisposable EnterContext(ICollectiveScope scope) {
      EnterCount++;
      return new _exit(this);
    }
    private sealed class _exit(_stubResolver r) : IDisposable {
      public void Dispose() => r.ExitCount++;
    }
  }

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, string methodName)
    where TEvent : ICollectiveEvent {
    // Type-erased Invoker mirrors what the source generator (Slice 5) emits.
    Func<object, ICollectiveEvent, object> invoker =
      (handler, evt) => ((_handler)handler).Apply((_typeA)(ICollectiveEvent)evt);
    return new CollectiveApplyEntry(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: typeof(_handler),
      MethodName: methodName,
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: invoker);
  }

  private static _ctx _newCtx() {
    var options = new DbContextOptionsBuilder<_ctx>()
      .UseInMemoryDatabase($"applier-{Guid.NewGuid():N}")
      .Options;
    return new _ctx(options);
  }

  private sealed class _ctx(DbContextOptions<_ctx> opts) : DbContext(opts) {
    // Intentionally bare — the tests we ship in Slice 7 unit-cover the
    // applier's validation + composition path; they never reach the SQL
    // execution so the missing DbSet does not matter. Real-Postgres
    // integration (full SQL UPDATE) is the natural follow-up.
  }
}
