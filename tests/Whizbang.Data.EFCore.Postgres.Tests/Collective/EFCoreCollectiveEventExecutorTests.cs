#pragma warning disable CA1707

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Locks the non-generic <see cref="ICollectiveEventExecutor"/> seam
/// the worker dispatch loop calls (Slice 7b). Each
/// <see cref="EFCoreCollectiveEventExecutor{TModel}"/> closes over the
/// concrete <c>TModel</c> at construction time so the worker can fan
/// out by <see cref="ICollectiveEventExecutor.ModelType"/> without ever
/// reaching for <see cref="Type.MakeGenericType(Type[])"/> at runtime —
/// AOT-clean by construction.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class EFCoreCollectiveEventExecutorTests {

  // ── ModelType discriminator ────────────────────────────────────────────

  [Test]
  public async Task ModelType_ReportsTheClosedGenericArgumentAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<_jobModel>();

    await Assert.That(exec.ModelType).IsEqualTo(typeof(_jobModel))
      .Because("The worker filters IEnumerable<ICollectiveEventExecutor> by entry.ModelType — the discriminator MUST be the closed generic argument or the lookup misses entirely.");
  }

  [Test]
  public async Task ModelType_DifferentTModel_DifferentDiscriminatorAsync() {
    ICollectiveEventExecutor a = new EFCoreCollectiveEventExecutor<_jobModel>();
    ICollectiveEventExecutor b = new EFCoreCollectiveEventExecutor<_otherModel>();

    await Assert.That(a.ModelType).IsNotEqualTo(b.ModelType)
      .Because("Two executors over different TModels MUST advertise different ModelType discriminators — otherwise the worker's IEnumerable filter would route _jobModel events to a _otherModel executor.");
  }

  // ── ApplyAsync delegates into the generic applier ──────────────────────

  [Test]
  public async Task ApplyAsync_NonDbContextSession_ThrowsArgumentExceptionAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<_jobModel>();
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []);
    var resolver = new _stubResolver("tenant");

    await Assert.That(() => exec.ApplyAsync(
        entry, new _handler(), evt, resolver,
        dbContextOrSession: "not a DbContext", // wrong type
        collectiveEventId: Guid.NewGuid(),
        cancellationToken: default))
      .ThrowsExactly<ArgumentException>()
      .Because("The EF executor casts dbContextOrSession to DbContext; a mismatched type signals the worker handed in a Dapper session or null. Clear ArgumentException beats InvalidCastException with no parameter context.");
  }

  [Test]
  public async Task ApplyAsync_NullDbContext_ThrowsArgumentNullAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<_jobModel>();
    var entry = _entryFor<_typeA>(typeof(_jobModel), "Apply");
    var evt = new _typeA(new _tenantScope("t"), []);
    var resolver = new _stubResolver("tenant");

    await Assert.That(() => exec.ApplyAsync(
        entry, new _handler(), evt, resolver,
        dbContextOrSession: null!,
        collectiveEventId: Guid.NewGuid(),
        cancellationToken: default))
      .ThrowsExactly<ArgumentNullException>();
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

  private sealed class _handler {
    public ICollectiveSpec<_jobModel> Apply(_typeA _) =>
      new _stubSpec(s => s.SetProperty(j => j.Status, "x"));
  }

  private sealed class _stubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => row => true;
    public IDisposable EnterContext(ICollectiveScope scope) => new _disposable();
    private sealed class _disposable : IDisposable { public void Dispose() { } }
  }

  private sealed record _stubSpec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters) : ICollectiveSpec<_jobModel>;

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, string methodName) =>
    new(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: typeof(_handler),
      MethodName: methodName,
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (handler, evt) => ((_handler)handler).Apply((_typeA)evt));
}
