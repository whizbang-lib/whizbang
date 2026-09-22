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
[Category("Shard4")]
public class CollectiveEventApplierTests {

  // ── Type-mismatch guards ───────────────────────────────────────────────

  [Test]
  public async Task ApplyAsync_EventTypeMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<TypeA>(typeof(JobModel), "Apply");
    var evt = new TypeB(new TenantScope("t"));
    var resolver = new StubResolver("tenant");
    await using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<JobModel>.ApplyAsync(
        entry, new Handler(), evt, resolver, ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<ArgumentException>()
      .Because("Entry registered for TypeA but dispatched a TypeB — that's a registry routing bug, not a domain condition.");
  }

  [Test]
  public async Task ApplyAsync_ModelTypeMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<TypeA>(typeof(OtherModel), "Apply"); // mismatched TModel
    var evt = new TypeA(new TenantScope("t"));
    var resolver = new StubResolver("tenant");
    await using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<JobModel>.ApplyAsync(
        entry, new Handler(), evt, resolver, ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<ArgumentException>()
      .Because("Dispatching to CollectiveEventApplier<JobModel> with an entry whose ModelType is OtherModel means the type-fanout in the upstream dispatcher is wrong.");
  }

  [Test]
  public async Task ApplyAsync_ScopeKindMismatch_ThrowsArgumentExceptionAsync() {
    var entry = _entryFor<TypeA>(typeof(JobModel), "Apply");
    var evt = new TypeA(new TenantScope("t"));
    var resolver = new StubResolver("workspace"); // wrong kind
    await using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<JobModel>.ApplyAsync(
        entry, new Handler(), evt, resolver, ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<ArgumentException>()
      .Because("DI should have dispatched the 'tenant' event to the tenant resolver — a mismatched resolver means the registry lookup is wrong.");
  }

  // ── Null-guard contract ────────────────────────────────────────────────

  [Test]
  public async Task ApplyAsync_NullEntry_ThrowsArgumentNullAsync() {
    var evt = new TypeA(new TenantScope("t"));
    await using var ctx = _newCtx();
    await Assert.That(() => CollectiveEventApplier<JobModel>.ApplyAsync(
        null!, new Handler(), evt, new StubResolver("tenant"), ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ApplyAsync_NullHandlerInstance_ThrowsArgumentNullAsync() {
    var entry = _entryFor<TypeA>(typeof(JobModel), "Apply");
    var evt = new TypeA(new TenantScope("t"));
    await using var ctx = _newCtx();
    await Assert.That(() => CollectiveEventApplier<JobModel>.ApplyAsync(
        entry, null!, evt, new StubResolver("tenant"), ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── EnterContext / handler-invocation behavior is covered end-to-end
  //    by CollectiveDispatcherEFCoreIntegrationTests (real Postgres).
  //    EF Core InMemory doesn't support ExecuteUpdateAsync, so the
  //    SQL-reaching behavior can't be unit-tested here in isolation.

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class JobModel {
    public string Status { get; set; } = string.Empty;
  }
  private sealed class OtherModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed record TenantScope(string TenantId) : CollectiveScope {
    public override string ScopeKind => "tenant";
  }

  private sealed record TypeA(CollectiveScope Scope) : ICollectiveEvent;
  private sealed record TypeB(CollectiveScope Scope) : ICollectiveEvent;

  private sealed class Handler {
    public int InvocationCount { get; private set; }
    public ICollectiveEvent? LastEvent { get; private set; }
    public ICollectiveSpec<JobModel> Apply(TypeA e) {
      InvocationCount++;
      LastEvent = e;
      return new Spec();
    }
  }

  private sealed class Spec : ICollectiveSpec<JobModel> {
    public Expression<Action<ICollectiveSetters<JobModel>>> Setters { get; } =
      s => s.SetProperty(j => j.Status, "X");
  }

  private sealed class StubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public int EnterCount { get; private set; }
    public int ExitCount { get; private set; }
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;
    public IDisposable EnterContext(ICollectiveScope scope) {
      EnterCount++;
      return new Exit(this);
    }
    private sealed class Exit(StubResolver r) : IDisposable {
      public void Dispose() => r.ExitCount++;
    }
  }

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, string methodName)
    where TEvent : ICollectiveEvent {
    // Type-erased Invoker mirrors what the source generator (Slice 5) emits.
    object invoker(object handler, ICollectiveEvent evt, ICollectiveQuery _) => ((Handler)handler).Apply((TypeA)evt);
    return new CollectiveApplyEntry(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: typeof(Handler),
      MethodName: methodName,
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: invoker);
  }

  private static Ctx _newCtx() {
    var options = new DbContextOptionsBuilder<Ctx>()
      .UseInMemoryDatabase($"applier-{Guid.NewGuid():N}")
      .Options;
    return new Ctx(options);
  }

  private sealed class Ctx(DbContextOptions<Ctx> opts) : DbContext(opts) {
  }
}
