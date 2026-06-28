#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type, not the concrete record

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Locks the contract of <see cref="CollectiveDispatcher"/> — the seam
/// the projection worker calls once per inbound
/// <see cref="ICollectiveEvent"/>. Composes the four moving parts the
/// previous slices delivered into one call:
/// </summary>
/// <list type="number">
///   <item><description>Find the right <see cref="ICollectiveScopeResolver"/> by <see cref="ICollectiveScope.ScopeKind"/>.</description></item>
///   <item><description>Find every <see cref="CollectiveApplyEntry"/> whose <see cref="CollectiveApplyEntry.EventType"/> matches the event's runtime type (multiple perspectives can react to one collective event).</description></item>
///   <item><description>For each entry — find the right <see cref="ICollectiveEventExecutor"/> by <see cref="ICollectiveEventExecutor.ModelType"/>, resolve the handler from <see cref="IServiceProvider"/>, and invoke the executor.</description></item>
///   <item><description>Aggregate the affected-row counts into a single <see cref="CollectiveDispatchResult"/>.</description></item>
/// </list>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveDispatcherTests {

  // ── Happy path: one resolver, one entry, one executor ──────────────────

  [Test]
  public async Task DispatchAsync_OneEntry_InvokesExecutorOnceAsync() {
    var executor = new _stubExecutor(typeof(_jobModel), affectedRows: 7);
    var dispatcher = _build(
      entries: [_entryFor<_archive>(typeof(_jobModel), typeof(_jobHandler))],
      resolvers: [new _stubResolver("tenant")],
      executors: [executor],
      handlers: [new _jobHandler()]);

    var result = await dispatcher.DispatchAsync(
      evt: new _archive(new _tenantScope("t-1"), [Guid.NewGuid()]),
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: new object(),
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(1)
      .Because("Exactly one entry matched the event type — exactly one handler was invoked.");
    await Assert.That(result.AffectedRowCount).IsEqualTo(7)
      .Because("The aggregate reflects the executor's reported affected-row count so the runner can log / surface it as a metric.");
    await Assert.That(executor.InvokeCount).IsEqualTo(1)
      .Because("Dispatcher must invoke the matching executor exactly once per entry.");
  }

  // ── Multi-perspective fan-out ─────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_TwoEntriesSameEventDifferentModels_FansOutAsync() {
    var jobExecutor = new _stubExecutor(typeof(_jobModel), affectedRows: 3);
    var profileExecutor = new _stubExecutor(typeof(_profileModel), affectedRows: 2);
    var dispatcher = _build(
      entries: [
        _entryFor<_archive>(typeof(_jobModel), typeof(_jobHandler)),
        _entryFor<_archive>(typeof(_profileModel), typeof(_profileHandler)),
      ],
      resolvers: [new _stubResolver("tenant")],
      executors: [jobExecutor, profileExecutor],
      handlers: [new _jobHandler(), new _profileHandler()]);

    var result = await dispatcher.DispatchAsync(
      evt: new _archive(new _tenantScope("t-1"), [Guid.NewGuid()]),
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: new object(),
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(2)
      .Because("Both perspectives subscribed to the same event type — both must fire.");
    await Assert.That(result.AffectedRowCount).IsEqualTo(5)
      .Because("Aggregate must sum across executors (3 + 2 = 5) so the runner sees total affected rows.");
    await Assert.That(jobExecutor.InvokeCount).IsEqualTo(1);
    await Assert.That(profileExecutor.InvokeCount).IsEqualTo(1);
  }

  // ── No matching entry ─────────────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_NoMatchingEntry_ReturnsZeroAsync() {
    var executor = new _stubExecutor(typeof(_jobModel), affectedRows: 99);
    var dispatcher = _build(
      entries: [_entryFor<_otherEvent>(typeof(_jobModel), typeof(_jobHandler))],
      resolvers: [new _stubResolver("tenant")],
      executors: [executor],
      handlers: [new _jobHandler()]);

    var result = await dispatcher.DispatchAsync(
      evt: new _archive(new _tenantScope("t-1"), [Guid.NewGuid()]),
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: new object(),
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(0)
      .Because("No registered handler is interested in this event type — dispatch is a no-op, not an error. Producers may emit events with no current subscribers (e.g. before a perspective is deployed); the runner shouldn't reject those.");
    await Assert.That(result.AffectedRowCount).IsEqualTo(0);
    await Assert.That(executor.InvokeCount).IsEqualTo(0)
      .Because("The unrelated executor MUST NOT be invoked just because it exists.");
  }

  // ── Resolver lookup failures ──────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_NoResolverForScopeKind_ThrowsAsync() {
    var dispatcher = _build(
      entries: [_entryFor<_archive>(typeof(_jobModel), typeof(_jobHandler))],
      resolvers: [new _stubResolver("workspace")], // wrong kind
      executors: [new _stubExecutor(typeof(_jobModel), 0)],
      handlers: [new _jobHandler()]);

    await Assert.That(async () => {
      _ = await dispatcher.DispatchAsync(
        evt: new _archive(new _tenantScope("t-1"), [Guid.NewGuid()]),
        collectiveEventId: Guid.NewGuid(),
        dbContextOrSession: new object(),
        cancellationToken: default);
    })
      .ThrowsExactly<InvalidOperationException>()
      .Because("Scope kinds are part of the contract — if a producer emits a 'tenant'-scoped event and the consumer registered no tenant resolver, that's a configuration bug and the runner must surface it loudly, not silently drop the event.");
  }

  // ── Executor lookup failures ──────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_NoExecutorForModelType_ThrowsAsync() {
    var dispatcher = _build(
      entries: [_entryFor<_archive>(typeof(_jobModel), typeof(_jobHandler))],
      resolvers: [new _stubResolver("tenant")],
      executors: [new _stubExecutor(typeof(_profileModel), 0)], // wrong model
      handlers: [new _jobHandler()]);

    await Assert.That(async () => {
      _ = await dispatcher.DispatchAsync(
        evt: new _archive(new _tenantScope("t-1"), [Guid.NewGuid()]),
        collectiveEventId: Guid.NewGuid(),
        dbContextOrSession: new object(),
        cancellationToken: default);
    })
      .ThrowsExactly<InvalidOperationException>()
      .Because("Per-TModel executor registration is part of the contract; missing the executor for a registered handler is a wiring bug, not a domain condition.");
  }

  // ── Null guards ───────────────────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_NullEvent_ThrowsArgumentNullAsync() {
    var dispatcher = _build([], [], [], []);
    await Assert.That(async () => {
      _ = await dispatcher.DispatchAsync(
        evt: null!,
        collectiveEventId: Guid.NewGuid(),
        dbContextOrSession: new object(),
        cancellationToken: default);
    })
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task DispatchAsync_NullDbContextOrSession_ThrowsArgumentNullAsync() {
    var dispatcher = _build([], [], [], []);
    await Assert.That(async () => {
      _ = await dispatcher.DispatchAsync(
        evt: new _archive(new _tenantScope("t-1"), []),
        collectiveEventId: Guid.NewGuid(),
        dbContextOrSession: null!,
        cancellationToken: default);
    })
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
  }

  private sealed class _profileModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed record _tenantScope(string TenantId) : CollectiveScope {
    public override string ScopeKind => "tenant";
  }

  private sealed record _archive(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;
  private sealed record _otherEvent(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed class _jobHandler {
    public ICollectiveSpec<_jobModel> Apply(_archive _) =>
      new _stubSpec<_jobModel>(s => s.SetProperty(j => j.Status, "x"));
  }
  private sealed class _profileHandler {
    public ICollectiveSpec<_profileModel> Apply(_archive _) =>
      new _stubSpec<_profileModel>(s => s.SetProperty(p => p.Name, "x"));
  }

  private sealed record _stubSpec<T>(Expression<Action<ICollectiveSetters<T>>> Setters) : ICollectiveSpec<T>
    where T : class;

  private sealed class _stubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => row => true;
    public IDisposable EnterContext(ICollectiveScope scope) => new _disposable();
    private sealed class _disposable : IDisposable { public void Dispose() { } }
  }

  private sealed class _stubExecutor(Type modelType, int affectedRows) : ICollectiveEventExecutor {
    public Type ModelType { get; } = modelType;
    public int InvokeCount { get; private set; }
    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry,
        object handlerInstance,
        ICollectiveEvent evt,
        ICollectiveScopeResolver resolver,
        object dbContextOrSession,
        Guid collectiveEventId,
        CancellationToken cancellationToken) {
      InvokeCount++;
      return Task.FromResult(affectedRows);
    }
  }

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, Type handlerType) =>
    new(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: handlerType,
      MethodName: "Apply",
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (handler, evt) => handler); // dispatcher tests don't exercise the invoker shape

  private static CollectiveDispatcher _build(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IReadOnlyList<ICollectiveScopeResolver> resolvers,
      IReadOnlyList<ICollectiveEventExecutor> executors,
      IReadOnlyList<object> handlers) {
    var services = new ServiceCollection();
    foreach (var h in handlers) {
      services.AddSingleton(h.GetType(), _ => h);
    }
    return new CollectiveDispatcher(services.BuildServiceProvider(), entries, resolvers, executors);
  }
}
