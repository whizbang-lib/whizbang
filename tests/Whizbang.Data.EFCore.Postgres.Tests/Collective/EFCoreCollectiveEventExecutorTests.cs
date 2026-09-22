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
[Category("Shard3")]
public class EFCoreCollectiveEventExecutorTests {

  // ── ModelType discriminator ────────────────────────────────────────────

  [Test]
  public async Task ModelType_ReportsTheClosedGenericArgumentAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<JobModel>();

    await Assert.That(exec.ModelType).IsEqualTo(typeof(JobModel))
      .Because("The worker filters IEnumerable<ICollectiveEventExecutor> by entry.ModelType — the discriminator MUST be the closed generic argument or the lookup misses entirely.");
  }

  [Test]
  public async Task ModelType_DifferentTModel_DifferentDiscriminatorAsync() {
    ICollectiveEventExecutor a = new EFCoreCollectiveEventExecutor<JobModel>();
    ICollectiveEventExecutor b = new EFCoreCollectiveEventExecutor<OtherModel>();

    await Assert.That(a.ModelType).IsNotEqualTo(b.ModelType)
      .Because("Two executors over different TModels MUST advertise different ModelType discriminators — otherwise the worker's IEnumerable filter would route JobModel events to a OtherModel executor.");
  }

  // ── ApplyAsync delegates into the generic applier ──────────────────────

  [Test]
  public async Task ApplyAsync_NonDbContextSession_ThrowsArgumentExceptionAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<JobModel>();
    var entry = _entryFor<TypeA>(typeof(JobModel), "Apply");
    var evt = new TypeA(new TenantScope("t"), []);
    var resolver = new StubResolver("tenant");

    await Assert.That(() => exec.ApplyAsync(
        entry, new Handler(), evt, resolver,
        dbContextOrSession: "not a DbContext", // wrong type
        collectiveEventId: Guid.NewGuid(),
        cancellationToken: default))
      .ThrowsExactly<ArgumentException>()
      .Because("The EF executor casts dbContextOrSession to DbContext; a mismatched type signals the worker handed in a Dapper session or null. Clear ArgumentException beats InvalidCastException with no parameter context.");
  }

  [Test]
  public async Task ApplyAsync_NullDbContext_ThrowsArgumentNullAsync() {
    ICollectiveEventExecutor exec = new EFCoreCollectiveEventExecutor<JobModel>();
    var entry = _entryFor<TypeA>(typeof(JobModel), "Apply");
    var evt = new TypeA(new TenantScope("t"), []);
    var resolver = new StubResolver("tenant");

    await Assert.That(() => exec.ApplyAsync(
        entry, new Handler(), evt, resolver,
        dbContextOrSession: null!,
        collectiveEventId: Guid.NewGuid(),
        cancellationToken: default))
      .ThrowsExactly<ArgumentNullException>();
  }

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

  private sealed record TypeA(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed class Handler {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    public ICollectiveSpec<JobModel> Apply(TypeA _) =>
      new StubSpec(s => s.SetProperty(j => j.Status, "x"));
  }

  private sealed class StubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;
    public IDisposable EnterContext(ICollectiveScope scope) => new Disposable();
    private sealed class Disposable : IDisposable { public void Dispose() { } }
  }

  private sealed record StubSpec(Expression<Action<ICollectiveSetters<JobModel>>> Setters) : ICollectiveSpec<JobModel>;

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, string methodName) =>
    new(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: typeof(Handler),
      MethodName: methodName,
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (handler, evt, _) => ((Handler)handler).Apply((TypeA)evt));
}
