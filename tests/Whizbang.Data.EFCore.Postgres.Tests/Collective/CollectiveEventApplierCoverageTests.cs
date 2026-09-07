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
/// Coverage for the one <see cref="CollectiveEventApplier{TModel}"/> guard
/// <see cref="CollectiveEventApplierTests"/> never exercises: a generator-emitted (or hand-rolled)
/// <c>Invoker</c> whose handler method returns something other than an
/// <see cref="ICollectiveSpec{TModel}"/>. This happens entirely in-memory, before any SQL is
/// compiled or a connection touched — <see cref="CollectiveEventApplier{TModel}.ApplyAsync"/> throws
/// on this branch before <c>EFCoreCollectiveAdapter.ExecuteAsync</c> is ever called, so a plain EF
/// Core InMemory <see cref="DbContext"/> (never queried) is enough. No database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Collective/CollectiveEventApplier.cs</code-under-test>
[Category("Shard1")]
public class CollectiveEventApplierCoverageTests {

  // If the source generator's Invoker cast were ever wrong (or a hand-authored Invoker for a test
  // double returned the wrong thing), silently proceeding would compile a default/empty UPDATE and
  // report the collective apply as having succeeded — leaving the perspective's projection state
  // permanently un-mutated for events that appeared to process cleanly.
  [Test]
  public async Task ApplyAsync_InvokerReturnsNonSpec_ThrowsInvalidOperationExceptionAsync() {
    Func<object, ICollectiveEvent, ICollectiveQuery, object> invoker =
      (_, _, _) => new object(); // not an ICollectiveSpec<_jobModel>
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel),
      EventType: typeof(_evt),
      HandlerType: typeof(_handler),
      MethodName: "Apply",
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: invoker);
    var evt = new _evt(new _tenantScope("t"));
    var resolver = new _stubResolver("tenant");
    using var ctx = _newCtx();

    await Assert.That(() => CollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), evt, resolver, ctx, Guid.NewGuid(), CollectiveApplyOptions.Default))
      .ThrowsExactly<InvalidOperationException>()
      .Because("the Invoker's return value must be the entry's declared ICollectiveSpec<TModel> — "
             + "anything else means the generator's Invoker shape or the handler is broken, and "
             + "proceeding to compile SQL from it would silently corrupt the projection.");
  }

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
  }

  private sealed record _tenantScope(string TenantId) : CollectiveScope {
    public override string ScopeKind => "tenant";
  }

  private sealed record _evt(CollectiveScope Scope) : ICollectiveEvent;

  private sealed class _handler {
    public ICollectiveSpec<_jobModel> Apply(_evt e) => new _spec();
  }

  private sealed class _spec : ICollectiveSpec<_jobModel> {
    public Expression<Action<ICollectiveSetters<_jobModel>>> Setters { get; } =
      s => s.SetProperty(j => j.Status, "X");
  }

  private sealed class _stubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;
    public IDisposable EnterContext(ICollectiveScope scope) => new _exit();
    private sealed class _exit : IDisposable {
      public void Dispose() { }
    }
  }

  private static _ctx _newCtx() {
    var options = new DbContextOptionsBuilder<_ctx>()
      .UseInMemoryDatabase($"applier-coverage-{Guid.NewGuid():N}")
      .Options;
    return new _ctx(options);
  }

  private sealed class _ctx(DbContextOptions<_ctx> opts) : DbContext(opts) {
    public DbSet<PerspectiveRow<_jobModel>> Jobs => Set<PerspectiveRow<_jobModel>>();
  }
}
