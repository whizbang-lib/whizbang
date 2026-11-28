// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
[Category("Shard1")]
public class EFCoreCollectiveDiUnitTests {

  private sealed class JobModel { public string Status { get; set; } = ""; }

  private sealed class Ctx(DbContextOptions<EFCoreCollectiveDiUnitTests.Ctx> options) : DbContext(options) {
  }

  private static Ctx _newCtx() => new(new DbContextOptionsBuilder<Ctx>().Options);

  private sealed record Evt : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }

  // ── EFCoreCollectiveSessionAccessor ────────────────────────────────────

  [Test]
  public async Task SessionAccessor_ReturnsTheRegisteredDbContextAsync() {
    var ctx = _newCtx();
    var sp = new ServiceCollection().AddSingleton(ctx).BuildServiceProvider();
    var session = new EFCoreCollectiveSessionAccessor<Ctx>().GetSession(sp);
    await Assert.That(session).IsSameReferenceAs(ctx);
  }

  [Test]
  public async Task SessionAccessor_ForAContextType_ReturnsTheRegisteredDbContextAsync() {
    var ctx = _newCtx();
    var sp = new ServiceCollection().AddSingleton(ctx).BuildServiceProvider();

    var session = new EFCoreCollectiveSessionAccessor(typeof(Ctx)).GetSession(sp);

    await Assert.That(session).IsSameReferenceAs(ctx);
    await Assert.That(() => new EFCoreCollectiveSessionAccessor(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new EFCoreCollectiveSessionAccessor(typeof(Ctx)).GetSession(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddCollectiveEventsEFCore((Type)null!)).Throws<ArgumentNullException>();
  }

  // ── EFCoreCollectiveEventExecutor ──────────────────────────────────────

  [Test]
  public async Task Executor_ReportsModelTypeAsync() {
    await Assert.That(new EFCoreCollectiveEventExecutor<JobModel>().ModelType).IsEqualTo(typeof(JobModel));
  }

  [Test]
  public async Task Executor_NonDbContextSession_ThrowsArgumentAsync() {
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(JobModel), EventType: typeof(Evt), HandlerType: typeof(object),
      MethodName: "x", ScopeHandling: CollectiveScopeHandling.Framework, SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (_, _, _) => null!);
    await Assert.That(() => new EFCoreCollectiveEventExecutor<JobModel>().ApplyAsync(
        entry, new object(), new Evt { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), dbContextOrSession: "not-a-dbcontext", Guid.NewGuid(), default))
      .Throws<ArgumentException>();
  }

  // ── DI extensions ──────────────────────────────────────────────────────

  [Test]
  public async Task AddCollectiveEventsEFCore_RegistersDispatcherResolverAccessorAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_newCtx());
    services.AddCollectiveEventsEFCore<Ctx>();
    services.AddCollectiveExecutorEFCore<JobModel>();
    var sp = services.BuildServiceProvider();

    await Assert.That(sp.GetService<ICollectiveDispatcher>()).IsNotNull();
    await Assert.That(sp.GetService<ICollectiveSessionAccessor>()).IsTypeOf<EFCoreCollectiveSessionAccessor<Ctx>>();
    await Assert.That(sp.GetServices<ICollectiveScopeResolver>().Any(r => r.ScopeKind == "tenant")).IsTrue();
    await Assert.That(sp.GetServices<ICollectiveScopeResolver>().Any(r => r.ScopeKind == FanOutScope.KIND)).IsTrue()
      .Because("#2620: the perspectives' fan-outs run through the same dispatcher, scoped to their key and tenant");
    await Assert.That(sp.GetServices<ICollectiveEventExecutor>().Any(e => e.ModelType == typeof(JobModel))).IsTrue();
  }

  // #2622: the late-apply rewinds run against the driver's database; a host with none (a context registered by hand)
  // gets no rewinds, which the worker reads as nothing to rewind.
  [Test]
  public async Task AddCollectiveEventsEFCore_RewindsLateFanOuts_OnlyWhenTheHostHasTheDriversDatabaseAsync() {
    var withDatabase = new ServiceCollection();
    withDatabase.AddSingleton(new Whizbang.Data.Postgres.PostgresArchiveConnection(
      static _ => throw new InvalidOperationException("the registration opens no connection"), Schema: null));
    withDatabase.AddCollectiveEventsEFCore<Ctx>();
    var withoutDatabase = new ServiceCollection();
    withoutDatabase.AddCollectiveEventsEFCore<Ctx>();

    await Assert.That(withDatabase.BuildServiceProvider().GetService<IFanOutLateApplyRewinds>())
      .IsTypeOf<Whizbang.Data.Postgres.Collective.PostgresFanOutLateApplyRewinds>();
    await Assert.That(withoutDatabase.BuildServiceProvider().GetService<IFanOutLateApplyRewinds>()).IsNull();
  }

  [Test]
  public async Task AddCollectiveEventsEFCore_NullEntries_ThrowsAsync() {
#pragma warning disable CS0618 // the hand-passed overload is obsolete; its guard still holds
    await Assert.That(() => new ServiceCollection().AddCollectiveEventsEFCore<Ctx>(null!))
      .Throws<ArgumentNullException>();
#pragma warning restore CS0618
  }
}
