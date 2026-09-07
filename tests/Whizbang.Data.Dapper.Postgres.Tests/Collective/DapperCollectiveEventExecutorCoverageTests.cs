using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Coverage for <see cref="DapperCollectiveEventExecutor{TModel}.ApplyAsync"/>'s forwarding call —
/// the sibling <see cref="DapperCollectiveUnitTests"/> suite covers <c>ModelType</c>, the
/// non-factory-session guard, and the null-table-name guard, but never a session that IS an
/// <see cref="IDbConnectionFactory"/>, so the actual delegation to
/// <see cref="DapperCollectiveEventApplier{TModel}.ApplyAsync"/> never runs. No database: both
/// cases here fail before (or exactly at) the point a connection would be opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventExecutor.cs</code-under-test>
public class DapperCollectiveEventExecutorCoverageTests {

  private sealed class _jobModel {
    public string Status { get; set; } = "";
  }

  private sealed record _evtA : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }
  private sealed record _evtB : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }

  private sealed class _handler {
    public _spec Apply(_evtA _) => new(s => s.SetProperty(j => j.Status, "x"));
  }

  private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters) : ICollectiveSpec<_jobModel>;

  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  private static CollectiveApplyEntry _entryFor<TEvent>() => new(
    ModelType: typeof(_jobModel), EventType: typeof(TEvent), HandlerType: typeof(_handler),
    MethodName: nameof(_handler.Apply), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, q) => ((_handler)h).Apply((_evtA)e));

  /// <summary>Throws once actually asked for a connection — never a shape the guards above should reach.</summary>
  private sealed class _factory : IDbConnectionFactory {
    public Task<System.Data.IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the stub factory only throws once a connection is actually requested");
  }

  // If the executor stopped forwarding to the applier (swallowed the call, or routed around it),
  // a collective event would silently fail to mutate the perspective it targets — the row would
  // never update while the caller sees no error at all.
  [Test]
  public async Task ApplyAsync_FactorySession_ForwardsTheCallToTheApplierAsync() {
    var executor = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job", _noSiblings);
    var mismatchedEntry = _entryFor<_evtB>(); // entry declares _evtB; the event instance is _evtA

    await Assert.That(() => executor.ApplyAsync(
        mismatchedEntry, new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), new _factory(), Guid.NewGuid(), default))
      .Throws<ArgumentException>()
      .Because("the applier's own event/entry type-mismatch guard must still fire when reached via the executor — proving the call actually forwarded rather than short-circuiting");
  }

  // Confirms the forwarding call reaches all the way to the applier's connection-open step (not
  // just its up-front validation guards) — if the executor duplicated or bypassed that step, a
  // real apply could silently skip opening a connection and never persist the mutation.
  [Test]
  public async Task ApplyAsync_MatchingEventAndFactorySession_ReachesTheApplierConnectionStepAsync() {
    var executor = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job", _noSiblings);

    await Assert.That(() => executor.ApplyAsync(
        _entryFor<_evtA>(), new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), new _factory(), Guid.NewGuid(), default))
      .Throws<InvalidOperationException>()
      .Because("past every validation guard the executor's forwarding call must reach the applier's actual connection-open step");
  }
}
