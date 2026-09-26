using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Services;

/// <summary>
/// The stranded-saga sweep re-arms the watchdog of a saga whose chain has ended, and only such a saga.
/// </summary>
/// <remarks>
/// <para>
/// A watchdog chain is armed once, when the saga starts; each tick arms the next. A lost tick ends the
/// chain and nothing ever looks at the saga again. The sweep closes that, under two rules that decide
/// every test here: it never wakes a saga that still has a tick coming (that would be early, and a
/// second chain beside the first), and it never wakes one that changed within the idle guard (it is
/// moving, or has a tick on the transport where the pending-wake check cannot see it).
/// </para>
/// <para>
/// The tick it arms starts at the stall limit, so on arrival it resolves stranded items rather than
/// waiting out a fresh stall count the saga has already served.
/// </para>
/// </remarks>
[Category("Unit")]
[Category("Saga")]
public class StrandedSagaSweepTests {

  private const string SAGA_NAME = "SweptSaga";
  private const string TENANT = "tenant-a";
  private static readonly Guid _entityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

  private static Guid _id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

  private static BaseSagaModel _saga(Guid id, DateTimeOffset updatedAt, int total = 3, bool dispatched = false, int completed = 0, int failed = 0) =>
    new() {
      Id = id,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      TotalItems = total,
      UpdatedAt = updatedAt,
      CompletionEventDispatched = dispatched,
      CompletedItems = completed,
      FailedItems = failed,
    };

  private static SagaItemModel _item(Guid sagaId, string id, SagaItemState state, DateTimeOffset updatedAt) =>
    new() { SagaId = sagaId, SagaName = SAGA_NAME, ItemIdentifier = id, State = state, UpdatedAt = updatedAt, StartedAt = updatedAt };

  private static DateTimeOffset _ago(TimeSpan span) => DateTimeOffset.UtcNow - span;

  [Test]
  public async Task Sweep_NoTickComingAndIdle_ArmsOneTickAtTheStallLimitInTheSagasTenantAsync() {
    var sagaId = _id(1);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new ItemRepository(new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1), [
      _item(sagaId, "a", SagaItemState.Completed, old),
      _item(sagaId, "b", SagaItemState.Completed, old),
      _item(sagaId, "c", SagaItemState.Running, old)]);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, repo, [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1);
    var once = emitter.Once.Single();
    await Assert.That(once.TenantId).IsEqualTo(TENANT)
      .Because("the tick must be handled in the saga's tenant, as the tick the saga armed for itself was");
    var tick = (SagaCompletionWatchdogTickEvent)once.Event;
    await Assert.That(tick.StreamId).IsEqualTo(sagaId);
    await Assert.That(tick.SagaName).IsEqualTo(SAGA_NAME);
    await Assert.That(tick.EntityId).IsEqualTo(_entityId);
    await Assert.That(tick.ConsecutiveStallCount).IsEqualTo(new SagaOptions().MaxConsecutiveStalls - 1)
      .Because("the saga has already been still for longer than a whole stall count; the tick resolves it on arrival");
    await Assert.That(tick.LastObservedCompleted).IsEqualTo(2);
    await Assert.That(tick.LastObservedFailed).IsEqualTo(0);
    await Assert.That(tick.LastObservedAt).IsNotNull();
  }

  [Test]
  public async Task Sweep_TickStillComing_ArmsNothingAsync() {
    var sagaId = _id(2);
    var old = _ago(TimeSpan.FromHours(2));
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(sagaId, old), [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid> { sagaId }), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0);
    await Assert.That(emitter.Once).IsEmpty()
      .Because("a saga with a tick coming has a live chain; a second tick would re-arm beside it forever");
  }

  [Test]
  public async Task Sweep_WakeLookupCannotTell_ArmsNothingAsync() {
    var sagaId = _id(3);
    var old = _ago(TimeSpan.FromHours(2));
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(sagaId, old), [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(null), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("not knowing whether a tick is coming must be treated as one coming");
  }

  [Test]
  public async Task Sweep_RecentItemActivity_ArmsNothingAsync() {
    var sagaId = _id(4);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter,
      _repoIdleSince(sagaId, _ago(TimeSpan.FromMinutes(1))),
      [new IncompleteSaga(_saga(sagaId, _ago(TimeSpan.FromHours(2))), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("an item changed a minute ago: the saga is moving, or its tick is on the transport where no table shows it");
  }

  [Test]
  public async Task Sweep_RecentSagaActivity_ArmsNothingAsync() {
    var sagaId = _id(5);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter,
      _repoIdleSince(sagaId, _ago(TimeSpan.FromHours(2))),
      [new IncompleteSaga(_saga(sagaId, _ago(TimeSpan.FromMinutes(1))), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0);
  }

  [Test]
  public async Task Sweep_SkipsCompletedAndEmptySagas_WithoutAskingAboutThemAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var wakes = new FixedWakes(new HashSet<Guid>());
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(_id(6), old), [
      new IncompleteSaga(_saga(_id(6), old, dispatched: true), TENANT),
      new IncompleteSaga(_saga(_id(7), old, total: 0), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0);
    await Assert.That(wakes.Asked).IsEmpty()
      .Because("a saga that completed, or never had items, has nothing to wake");
  }

  [Test]
  public async Task Sweep_AsksAboutEveryCandidateInOneLookupAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var wakes = new FixedWakes(new HashSet<Guid>());
    var svc = new SweptSagaService(new RecordingEmitter(), _repoIdleSince(_id(8), old), [
      new IncompleteSaga(_saga(_id(8), old), TENANT),
      new IncompleteSaga(_saga(_id(9), old), "tenant-b")]);

    var armed = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(armed).IsEqualTo(2);
    await Assert.That(wakes.Asked.Count).IsEqualTo(1)
      .Because("one query for the whole candidate set, not one per saga");
    await Assert.That(wakes.Asked[0]).IsEquivalentTo([_id(8), _id(9)]);
  }

  [Test]
  public async Task Sweep_SameIdleState_ClaimsTheSameKey_NewActivityANewKeyAsync() {
    var sagaId = _id(10);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = _repoIdleSince(sagaId, old);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, repo, [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);
    await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);
    repo.LastActivity = old.AddMinutes(30);
    await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    var keys = emitter.Once.ConvertAll(o => o.ClaimKey);
    await Assert.That(keys[0]).IsEqualTo(keys[1])
      .Because("every instance and every restart sweeping the same stopped saga must arrive at one emission");
    await Assert.That(keys[2]).IsNotEqualTo(keys[0])
      .Because("a saga that moved and stopped again is a new stop, owed one more tick");
    await Assert.That(keys[0]).Contains(sagaId.ToString("N"));
  }

  [Test]
  public async Task Sweep_LosingTheClaim_DoesNotCountAsArmedAsync() {
    var sagaId = _id(11);
    var old = _ago(TimeSpan.FromHours(2));
    var emitter = new RecordingEmitter { WinClaims = false };
    var svc = new SweptSagaService(emitter, _repoIdleSince(sagaId, old), [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("another instance won the claim and armed it");
  }

  [Test]
  public async Task Sweep_WithNoItemRepository_JudgesByTheSagaRowAsync() {
    var sagaId = _id(12);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, itemRepository: null,
      [new IncompleteSaga(_saga(sagaId, _ago(TimeSpan.FromHours(2)), completed: 1, failed: 1), null)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1);
    var once = emitter.Once.Single();
    await Assert.That(once.TenantId).IsNull();
    var tick = (SagaCompletionWatchdogTickEvent)once.Event;
    await Assert.That(tick.LastObservedCompleted).IsEqualTo(1);
    await Assert.That(tick.LastObservedFailed).IsEqualTo(1);
  }

  [Test]
  public async Task ArmedTick_OnArrival_ResolvesTheStrandedItemAsync() {
    var sagaId = _id(13);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new ItemRepository(new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1), [
      _item(sagaId, "done", SagaItemState.Completed, old),
      _item(sagaId, "lost", SagaItemState.Running, old)]);
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, repo, [new IncompleteSaga(_saga(sagaId, old, total: 2), TENANT)]);
    await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);
    var tick = (SagaCompletionWatchdogTickEvent)emitter.Once.Single().Event;

    await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(emitter.Published.OfType<TestItemFailedEvent>().Select(e => e.ItemIdentifier)).IsEquivalentTo(["lost"])
      .Because("the swept tick arrives at the stall limit and resolves the item whose worker is gone, instead of waiting out a fresh stall count");
  }

  [Test]
  public async Task ServiceWithoutALoader_ArmsNothingAsync() {
    var emitter = new RecordingEmitter();
    var svc = new SweptSagaService(emitter, itemRepository: null, incomplete: null);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("a saga service that does not enumerate its sagas behaves exactly as before the sweep existed");
  }

  [Test]
  public async Task RepositoryDefault_LastActivityIsTheNewestItemChangeAsync() {
    var sagaId = _id(14);
    var newest = _ago(TimeSpan.FromMinutes(7));
    ISagaItemRepository repo = new DefaultActivityRepository([
      _item(sagaId, "a", SagaItemState.Completed, _ago(TimeSpan.FromHours(3))),
      _item(sagaId, "b", SagaItemState.Running, newest)]);

    await Assert.That(await repo.GetLastActivityAsync(sagaId, CancellationToken.None)).IsEqualTo(newest);
  }

  [Test]
  public async Task RepositoryDefault_NoItems_HasNoLastActivityAsync() {
    ISagaItemRepository repo = new DefaultActivityRepository([]);

    await Assert.That(await repo.GetLastActivityAsync(_id(15), CancellationToken.None)).IsNull();
  }

  [Test]
  public async Task EmitterDefault_InTenant_ClaimsLikePublishOnceAsync() {
    ISagaEventEmitter emitter = new ScopelessEmitter();

    var won = await emitter.PublishOnceInTenantAsync(TENANT, "k", new SagaCompletionWatchdogTickEvent(), CancellationToken.None);

    await Assert.That(won).IsTrue();
    await Assert.That(((ScopelessEmitter)emitter).Claimed).IsEquivalentTo(["k"])
      .Because("an emitter with no notion of scope still claims, so a sweep through it never publishes twice");
  }

  [Test]
  public async Task ParticipantDefault_ArmsNothingAsync() {
    ISagaWatchdogParticipant participant = new MinimalParticipant();

    await Assert.That(await participant.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None)).IsEqualTo(0);
  }

  // ── Test doubles ───────────────────────────────────────────────────────

  private static ItemRepository _repoIdleSince(Guid sagaId, DateTimeOffset at) =>
    new(new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1), [_item(sagaId, "x", SagaItemState.Running, at)]) {
      LastActivity = at,
    };

  private sealed class FixedWakes(IReadOnlySet<Guid>? pending) : ISagaWakeLookup {
    public List<IReadOnlyList<Guid>> Asked { get; } = [];
    public Task<IReadOnlySet<Guid>?> WithPendingWakeAsync(IReadOnlyList<Guid> sagaIds, CancellationToken cancellationToken) {
      Asked.Add(sagaIds);
      return Task.FromResult(pending);
    }
  }

  private sealed class ItemRepository(SagaItemAggregate agg, IReadOnlyList<SagaItemModel> items) : ISagaItemRepository {
    public DateTimeOffset? LastActivity { get; set; } = items.Count == 0 ? null : items.Max(i => i.UpdatedAt);
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken) => Task.FromResult(agg);
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken) => Task.FromResult(items);
    public Task<DateTimeOffset?> GetLastActivityAsync(Guid sagaId, CancellationToken cancellationToken) => Task.FromResult(LastActivity);
  }

  private sealed class DefaultActivityRepository(IReadOnlyList<SagaItemModel> items) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(new SagaItemAggregate(items.Count, 0, 0, items.Count));
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken) => Task.FromResult(items);
  }

  private sealed record OnceCall(string? TenantId, string ClaimKey, IEvent Event);

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public bool WinClaims { get; init; } = true;
    public List<IEvent> Published { get; } = [];
    public List<OnceCall> Once { get; } = [];
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.CompletedTask;
    }
    public Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.CompletedTask;
    }
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.FromResult(true);
    }
    public Task<bool> PublishOnceInTenantAsync<TEvent>(string? tenantId, string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Once.Add(new OnceCall(tenantId, claimKey, eventData));
      return Task.FromResult(WinClaims);
    }
  }

  private sealed class ScopelessEmitter : ISagaEventEmitter {
    public List<string> Claimed { get; } = [];
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Claimed.Add(claimKey);
      return Task.FromResult(true);
    }
  }

  private sealed class MinimalParticipant : ISagaWatchdogParticipant {
    public string SagaName => SAGA_NAME;
    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken)
      => Task.FromResult(WatchdogTickOutcome.ReArmed);
  }

  private sealed class SweptSagaService(
      ISagaEventEmitter emitter,
      ISagaItemRepository? itemRepository,
      IReadOnlyList<IncompleteSaga>? incomplete)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, itemRepository, new NotTerminalReader(), options: null, NullLogger<SweptSagaService>.Instance) {

    protected override Task<IReadOnlyList<IncompleteSaga>> LoadIncompleteSagasAsync(CancellationToken cancellationToken)
      => incomplete is null ? base.LoadIncompleteSagasAsync(cancellationToken) : Task.FromResult(incomplete);

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(incomplete?.FirstOrDefault(i => i.Saga.Id == sagaId)?.Saga);

    protected override TestInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) => new();
    protected override TestItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) => new();
    protected override TestItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) => new();
    protected override TestItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) => new();
    protected override TestItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { ItemIdentifier = itemIdentifier, ErrorMessage = errorMessage };
    protected override TestCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) => new();
    protected override TestResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) => new();
    protected override TestHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) => new();
    protected override TestHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) => new();
  }

  private sealed class NotTerminalReader : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(SagaItemTerminalOutcome.NotTerminal);
  }

  private sealed class TestInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public IReadOnlyList<string>? HookNames { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class TestItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class TestItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class TestCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class TestResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class TestHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }
}
