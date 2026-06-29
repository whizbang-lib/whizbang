#pragma warning disable CA1707

using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the EF Core collective DI extensions, session accessor, and executor
/// session-cast guard.
/// </summary>
public class EFCoreCollectiveDiUnitTests {

  private sealed class _jobModel { public string Status { get; set; } = ""; }

  private sealed class _ctx : DbContext {
    public _ctx(DbContextOptions<_ctx> options) : base(options) { }
  }

  private static _ctx _newCtx() => new(new DbContextOptionsBuilder<_ctx>().Options);

  private sealed record _evt : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }

  // ── EFCoreCollectiveSessionAccessor ────────────────────────────────────

  [Test]
  public async Task SessionAccessor_ReturnsTheRegisteredDbContextAsync() {
    var ctx = _newCtx();
    var sp = new ServiceCollection().AddSingleton(ctx).BuildServiceProvider();
    var session = new EFCoreCollectiveSessionAccessor<_ctx>().GetSession(sp);
    await Assert.That(session).IsSameReferenceAs(ctx);
  }

  // ── EFCoreCollectiveEventExecutor ──────────────────────────────────────

  [Test]
  public async Task Executor_ReportsModelTypeAsync() {
    await Assert.That(new EFCoreCollectiveEventExecutor<_jobModel>().ModelType).IsEqualTo(typeof(_jobModel));
  }

  [Test]
  public async Task Executor_NonDbContextSession_ThrowsArgumentAsync() {
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel), EventType: typeof(_evt), HandlerType: typeof(object),
      MethodName: "x", ScopeHandling: CollectiveScopeHandling.Framework, SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (_, _, _) => null!);
    await Assert.That(() => new EFCoreCollectiveEventExecutor<_jobModel>().ApplyAsync(
        entry, new object(), new _evt { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), dbContextOrSession: "not-a-dbcontext", Guid.NewGuid(), default))
      .Throws<ArgumentException>();
  }

  // ── DI extensions ──────────────────────────────────────────────────────

  [Test]
  public async Task AddCollectiveEventsEFCore_RegistersDispatcherResolverAccessorAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_newCtx());
    services.AddCollectiveEventsEFCore<_ctx>(System.Array.Empty<CollectiveApplyEntry>());
    services.AddCollectiveExecutorEFCore<_jobModel>();
    var sp = services.BuildServiceProvider();

    await Assert.That(sp.GetService<ICollectiveDispatcher>()).IsNotNull();
    await Assert.That(sp.GetService<ICollectiveSessionAccessor>()).IsTypeOf<EFCoreCollectiveSessionAccessor<_ctx>>();
    await Assert.That(sp.GetServices<ICollectiveScopeResolver>().Any(r => r.ScopeKind == "tenant")).IsTrue();
    await Assert.That(sp.GetServices<ICollectiveEventExecutor>().Any(e => e.ModelType == typeof(_jobModel))).IsTrue();
  }

  [Test]
  public async Task AddCollectiveEventsEFCore_NullEntries_ThrowsAsync() {
    await Assert.That(() => new ServiceCollection().AddCollectiveEventsEFCore<_ctx>(null!))
      .Throws<ArgumentNullException>();
  }
}
