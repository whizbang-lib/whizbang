// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
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

  private static BaseSagaModel _saga(Guid id, DateTimeOffset updatedAt, int total = 3, bool dispatched = false, int completed = 0, int failed = 0, SagaStatus status = SagaStatus.Running) =>
    new() {
      Id = id,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      TotalItems = total,
      UpdatedAt = updatedAt,
      CompletionEventDispatched = dispatched,
      CompletedItems = completed,
      FailedItems = failed,
      Status = status,
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

  /// <summary>An abandoned saga is left alone, and is not even asked about.</summary>
  /// <remarks>
  /// Abandoning a saga is the decision that it is not coming back on its own. Re-arming it once per
  /// StrandedSagaRearmInterval published SagaCompletionAbandonedEvent again each time, which told an
  /// operator nothing new and made the event's count meaningless as a signal. Asserted on the wake
  /// lookup as well as the arm count, because a saga excluded from the candidate set should cost no
  /// query either.
  /// </remarks>
  [Test]
  public async Task Sweep_SkipsAnAbandonedSaga_WithoutAskingAboutItAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var wakes = new FixedWakes(new HashSet<Guid>());
    var svc = new SweptSagaService(new RecordingEmitter(), _repoIdleSince(_id(11), old), [
      new IncompleteSaga(_saga(_id(11), old, status: SagaStatus.Abandoned), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("the watchdog already gave up on it; re-arming re-publishes the abandonment");
    await Assert.That(wakes.Asked).IsEmpty()
      .Because("a saga that is out of the candidate set should not cost a wake lookup");
  }

  /// <summary>A still-running saga beside an abandoned one is still swept.</summary>
  /// <remarks>The exclusion has to be per saga, or one abandoned saga would stall the sweep for the rest.</remarks>
  [Test]
  public async Task Sweep_ArmsARunningSaga_BesideAnAbandonedOneAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var wakes = new FixedWakes(new HashSet<Guid>());
    var svc = new SweptSagaService(new RecordingEmitter(), _repoIdleSince(_id(12), old), [
      new IncompleteSaga(_saga(_id(12), old), TENANT),
      new IncompleteSaga(_saga(_id(13), old, status: SagaStatus.Abandoned), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1)
      .Because("the running saga is still stranded and still wants a tick");
    await Assert.That(wakes.Asked[0]).IsEquivalentTo([_id(12)])
      .Because("only the running one is a candidate");
  }

  /// <summary>
  /// A saga the watchdog abandoned stays abandoned, whether or not its perspective records it.
  /// </summary>
  /// <remarks>
  /// The saga here stays Running because its perspective does not apply the abandon event, which is
  /// the case the Abandoned status cannot reach. The abandonment claim can: the tick claims it when it
  /// abandons, and the sweep leaves a claimed saga alone however many intervals pass. Before, every
  /// interval brought a new sweep claim key, a new tick and a new abandon event.
  /// </remarks>
  [Test]
  public async Task Sweep_AbandonedSaga_IsNotReArmedInLaterIntervalsAsync() {
    var sagaId = _id(50);
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var options = new SagaOptions { TimeProvider = clock };
    var emitter = new ClaimingEmitter();
    var svc = new SweptSagaService(emitter, itemRepository: null,
      [new IncompleteSaga(_saga(sagaId, clock.Now - TimeSpan.FromHours(2)), TENANT)], options: options);
    var noTickComing = new FixedWakes(new HashSet<Guid>());

    await svc.ArmStrandedSagasAsync(noTickComing, CancellationToken.None);
    var outcome = await svc.TryRecoverViaWatchdogTickAsync(
      emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single(), CancellationToken.None);
    var armedLater = 0;
    for (var interval = 1; interval <= 5; interval++) {
      clock.Now += options.StrandedSagaRearmInterval;
      armedLater += await svc.ArmStrandedSagasAsync(noTickComing, CancellationToken.None);
    }

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Abandoned)
      .Because("the precondition: the sweep's tick arrives at the stall limit with nothing to resolve");
    await Assert.That(armedLater).IsEqualTo(0)
      .Because("an abandoned saga is not coming back on its own; re-arming it only publishes its abandonment again");
    await Assert.That(emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Count()).IsEqualTo(1);
    await Assert.That(emitter.Published.OfType<SagaCompletionAbandonedEvent>().Count()).IsEqualTo(1);
  }

  /// <summary>The claim holds one saga back, not the sagas swept beside it.</summary>
  [Test]
  public async Task Sweep_ArmsARunningSaga_BesideOneHoldingItsAbandonmentClaimAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var emitter = new ClaimingEmitter();
    emitter.Claim(SagaAbandonGuard.ClaimKey(SAGA_NAME, _id(52)));
    var wakes = new FixedWakes(new HashSet<Guid>());
    var svc = new SweptSagaService(emitter, _repoIdleSince(_id(51), old), [
      new IncompleteSaga(_saga(_id(51), old), TENANT),
      new IncompleteSaga(_saga(_id(52), old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1);
    await Assert.That(wakes.Asked.Single()).IsEquivalentTo([_id(51)])
      .Because("the claimed saga is out of the candidate set before any wake is looked up");
  }

  /// <summary>An emitter that cannot read claims leaves the sweep as it was.</summary>
  /// <remarks>
  /// The emitter's default answers that no key is claimed. The sweep then arms as it did before the
  /// claim existed: a saga it cannot see as abandoned is treated as stranded, which costs a repeated
  /// abandonment rather than a stranded saga left for good.
  /// </remarks>
  [Test]
  public async Task Sweep_EmitterThatCannotReadClaims_ArmsAsBeforeAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var emitter = new ScopelessEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(_id(53), old), [new IncompleteSaga(_saga(_id(53), old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1);
    await Assert.That(await ((ISagaEventEmitter)emitter).ReleaseClaimAsync("any", CancellationToken.None)).IsFalse()
      .Because("an emitter with no claim store has nothing to release");
  }

  /// <summary>
  /// An operator re-drives an abandoned saga: its claim is released and it gets a fresh watchdog chain
  /// with its whole stall budget.
  /// </summary>
  [Test]
  public async Task ReDrive_AbandonedSaga_ReleasesTheClaimAndArmsAFreshTickAsync() {
    var sagaId = _id(54);
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var options = new SagaOptions { TimeProvider = clock };
    var emitter = new ClaimingEmitter();
    emitter.Claim(SagaAbandonGuard.ClaimKey(SAGA_NAME, sagaId));
    var svc = new SweptSagaService(emitter, itemRepository: null,
      [new IncompleteSaga(_saga(sagaId, clock.Now - TimeSpan.FromHours(2)), TENANT)], options: options);

    var redriven = await svc.ReDriveAbandonedSagaAsync(new SagaContext(sagaId, _entityId), CancellationToken.None);
    clock.Now += options.StrandedSagaRearmInterval;
    var armedAfter = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(redriven).IsTrue();
    var tick = emitter.Plain.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(tick.StreamId).IsEqualTo(sagaId);
    await Assert.That(tick.SagaName).IsEqualTo(SAGA_NAME);
    await Assert.That(tick.EntityId).IsEqualTo(_entityId);
    await Assert.That(tick.ConsecutiveStallCount).IsEqualTo(0)
      .Because("a re-driven saga starts a whole stall budget over, not one stall from abandoning again");
    await Assert.That(tick.LastObservedAt).IsNull();
    await Assert.That(armedAfter).IsEqualTo(1)
      .Because("with the claim released the sweep treats the saga as any other again");
  }

  /// <summary>Re-driving a saga that is not abandoned does nothing, so it cannot start a second chain.</summary>
  [Test]
  public async Task ReDrive_SagaNotAbandoned_ArmsNothingAsync() {
    var emitter = new ClaimingEmitter();
    var svc = new SweptSagaService(emitter, itemRepository: null, []);

    var redriven = await svc.ReDriveAbandonedSagaAsync(new SagaContext(_id(55), _entityId), CancellationToken.None);

    await Assert.That(redriven).IsFalse();
    await Assert.That(emitter.Plain).IsEmpty()
      .Because("a saga with no abandonment claim still has its chain; a second tick would re-arm beside it");
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

  /// <summary>
  /// The claim records that a tick was PUBLISHED, not that it was HANDLED. A stranded saga never
  /// changes, so a claim keyed only on its last change made one lost tick its last (#935). The key
  /// also counts whole re-arm intervals of stillness, so the saga is owed one more tick per interval.
  /// </summary>
  [Test]
  public async Task Sweep_TickLostAndSagaStillStranded_IsReArmedAfterTheInterval_NotBeforeAsync() {
    var sagaId = _id(40);
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var lastChange = clock.Now - TimeSpan.FromMinutes(10);
    var options = new SagaOptions { StrandedSagaRearmInterval = TimeSpan.FromHours(1), TimeProvider = clock };
    var emitter = new ClaimingEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(sagaId, lastChange),
      [new IncompleteSaga(_saga(sagaId, lastChange), TENANT)], options: options);
    var noTickComing = new FixedWakes(new HashSet<Guid>());

    var first = await svc.ArmStrandedSagasAsync(noTickComing, CancellationToken.None);
    clock.Now += TimeSpan.FromMinutes(45);
    var withinTheInterval = await svc.ArmStrandedSagasAsync(noTickComing, CancellationToken.None);
    clock.Now += TimeSpan.FromMinutes(10);
    var pastTheInterval = await svc.ArmStrandedSagasAsync(noTickComing, CancellationToken.None);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(withinTheInterval).IsEqualTo(0)
      .Because("inside one re-arm interval the saga already has its tick, lost or not");
    await Assert.That(pastTheInterval).IsEqualTo(1)
      .Because("a whole interval of stillness later the first tick was evidently lost; the saga is owed another");
    await Assert.That(emitter.Published.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Sweep_SeveralInstancesInOneInterval_ArmOneTickAsync() {
    var sagaId = _id(41);
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var lastChange = clock.Now - TimeSpan.FromHours(5);
    var options = new SagaOptions { TimeProvider = clock };
    var emitter = new ClaimingEmitter();
    var instances = Enumerable.Range(0, 3).Select(_ => new SweptSagaService(emitter, _repoIdleSince(sagaId, lastChange),
      [new IncompleteSaga(_saga(sagaId, lastChange), TENANT)], options: options)).ToList();

    var armed = 0;
    foreach (var instance in instances) {
      armed += await instance.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);
      clock.Now += TimeSpan.FromMinutes(5);
    }

    await Assert.That(armed).IsEqualTo(1)
      .Because("every instance and restart sweeping the same stop in one interval arrives at one claim");
    await Assert.That(emitter.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Sweep_TickStillComing_IsNotReArmedHoweverManyIntervalsHavePassedAsync() {
    var sagaId = _id(42);
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var lastChange = clock.Now - TimeSpan.FromDays(3);
    var emitter = new ClaimingEmitter();
    var svc = new SweptSagaService(emitter, _repoIdleSince(sagaId, lastChange),
      [new IncompleteSaga(_saga(sagaId, lastChange), TENANT)], options: new SagaOptions { TimeProvider = clock });

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid> { sagaId }), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(0)
      .Because("a tick still waiting is a live chain; re-arming beside it would start a second chain");
    await Assert.That(emitter.Published).IsEmpty();
  }

  [Test]
  public async Task Options_RearmInterval_DefaultsToAnHour_AndMustBePositiveAsync() {
    var options = new SagaOptions();

    await Assert.That(options.StrandedSagaRearmInterval).IsEqualTo(TimeSpan.FromHours(1));
    await Assert.That(() => options.StrandedSagaRearmInterval = TimeSpan.Zero).Throws<ArgumentOutOfRangeException>()
      .Because("a zero interval would re-arm on every sweep");
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
  [NotInParallel("ScopeContextAccessor")]
  public async Task Sweep_ItemRepositoryRequiresTenantScope_ReadsInTheSagasTenantAndArmsTheTickAsync() {
    _clearAmbientScope();
    var sagaId = _id(16);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new TenantScopedItemRepository(new Dictionary<Guid, string?> { [sagaId] = TENANT }, old);
    var dispatcher = new RecordingDispatcher();
    var svc = new SweptSagaService(new DispatcherSagaEventEmitter(dispatcher), repo, [new IncompleteSaga(_saga(sagaId, old), TENANT)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1)
      .Because("the sweep runs on a maintenance worker with no scope; a repository reading through a tenant-scoped lens must still be readable");
    await Assert.That(repo.Reads).IsEquivalentTo([
      new ScopedRead(nameof(ISagaItemRepository.GetLastActivityAsync), sagaId, TENANT),
      new ScopedRead(nameof(ISagaItemRepository.GetAggregateForSagaAsync), sagaId, TENANT)]);
    var (_, publishedEvent, publishedScope) = dispatcher.PublishOnceCalls.Single();
    await Assert.That(publishedScope?.TenantId).IsEqualTo(TENANT);
    await Assert.That(((SagaCompletionWatchdogTickEvent)publishedEvent!).StreamId).IsEqualTo(sagaId);
    await Assert.That(Whizbang.Core.Security.ScopeContextAccessor.CurrentContext).IsNull()
      .Because("the tenant is established for the saga's work only, never left on the worker");
  }

  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task Sweep_SagasInDifferentTenants_EachIsReadAndArmedInItsOwnTenantAsync() {
    _clearAmbientScope();
    var first = _id(17);
    var second = _id(18);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new TenantScopedItemRepository(new Dictionary<Guid, string?> { [first] = "tenant-a", [second] = "tenant-b" }, old);
    var dispatcher = new RecordingDispatcher();
    var svc = new SweptSagaService(new DispatcherSagaEventEmitter(dispatcher), repo, [
      new IncompleteSaga(_saga(first, old), "tenant-a"),
      new IncompleteSaga(_saga(second, old), "tenant-b")]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(2);
    await Assert.That(repo.Reads).IsEquivalentTo([
      new ScopedRead(nameof(ISagaItemRepository.GetLastActivityAsync), first, "tenant-a"),
      new ScopedRead(nameof(ISagaItemRepository.GetAggregateForSagaAsync), first, "tenant-a"),
      new ScopedRead(nameof(ISagaItemRepository.GetLastActivityAsync), second, "tenant-b"),
      new ScopedRead(nameof(ISagaItemRepository.GetAggregateForSagaAsync), second, "tenant-b")])
      .Because("one sweep crosses tenants; each saga must be read in its own, never in the one before it");
    var armedIn = dispatcher.PublishOnceCalls.ToDictionary(
      c => ((SagaCompletionWatchdogTickEvent)c.Event!).StreamId, c => c.Scope?.TenantId);
    await Assert.That(armedIn[first]).IsEqualTo("tenant-a");
    await Assert.That(armedIn[second]).IsEqualTo("tenant-b");
  }

  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task Sweep_SagaWithNoTenant_IsReadInTheWorkersOwnContextAsync() {
    _clearAmbientScope();
    var sagaId = _id(19);
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new TenantScopedItemRepository(new Dictionary<Guid, string?> { [sagaId] = null }, old);
    var dispatcher = new RecordingDispatcher();
    var svc = new SweptSagaService(new DispatcherSagaEventEmitter(dispatcher), repo, [new IncompleteSaga(_saga(sagaId, old), null)]);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1);
    await Assert.That(repo.Reads.Select(r => r.TenantId)).IsEquivalentTo(new string?[] { null, null })
      .Because("a saga that is not tenant-scoped is read as it always was");
    await Assert.That(dispatcher.PublishOnceCalls.Single().Scope?.TenantId)
      .IsEqualTo(Whizbang.Core.Lenses.TenantConstants.AllTenants);
  }

  [Test]
  public async Task EmitterDefault_RunInTenant_RunsTheWorkAsIsAsync() {
    ISagaEventEmitter emitter = new ScopelessEmitter();
    using var cts = new CancellationTokenSource();

    var received = await emitter.RunInTenantAsync(TENANT, ct => Task.FromResult(ct), cts.Token);

    await Assert.That(received).IsEqualTo(cts.Token)
      .Because("an emitter with no notion of scope runs the work unchanged, as the sweep did before it read in the tenant");
  }

  [Test]
  public async Task EmitterDefault_RunInTenant_NullWork_ThrowsAsync() {
    ISagaEventEmitter emitter = new ScopelessEmitter();

    await Assert.That(async () => await emitter.RunInTenantAsync<int>(TENANT, null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Sweep_OneSagasReadThrows_TheOthersAreStillArmedAndTheFailureIsLoggedAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new FailingItemRepository(old) { FailFor = _id(21) };
    var emitter = new RecordingEmitter();
    var logger = new RecordingLogger();
    var svc = new SweptSagaService(emitter, repo, [
      new IncompleteSaga(_saga(_id(20), old), "tenant-a"),
      new IncompleteSaga(_saga(_id(21), old), "tenant-b"),
      new IncompleteSaga(_saga(_id(22), old), "tenant-c")], logger);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(2)
      .Because("one saga's bad read must not strand every saga swept after it");
    await Assert.That(emitter.Once.Select(o => ((SagaCompletionWatchdogTickEvent)o.Event).StreamId))
      .IsEquivalentTo([_id(20), _id(22)]);
    var entry = logger.Entries.Single();
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(entry.Message).Contains(SAGA_NAME);
    await Assert.That(entry.Message).Contains(_id(21).ToString());
    await Assert.That(entry.Message).Contains("tenant-b");
  }

  [Test]
  public async Task Sweep_CanceledMidSweep_StopsTheSweepAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    using var cts = new CancellationTokenSource();
    var repo = new FailingItemRepository(old) { CancelAt = _id(24), Cancellation = cts };
    var emitter = new RecordingEmitter();
    var logger = new RecordingLogger();
    var svc = new SweptSagaService(emitter, repo, [
      new IncompleteSaga(_saga(_id(23), old), TENANT),
      new IncompleteSaga(_saga(_id(24), old), TENANT),
      new IncompleteSaga(_saga(_id(25), old), TENANT)], logger);

    await Assert.That(async () => await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(repo.ReadSagas).DoesNotContain(_id(25))
      .Because("a canceled sweep stops; it is not a saga failure to log and step past");
    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task Sweep_OperationCanceledWithoutTheSweepBeingCanceled_IsTreatedAsThatSagasFailureAsync() {
    var old = _ago(TimeSpan.FromHours(2));
    var repo = new FailingItemRepository(old) { FailFor = _id(26), FailWith = new OperationCanceledException("a read timed out") };
    var emitter = new RecordingEmitter();
    var logger = new RecordingLogger();
    var svc = new SweptSagaService(emitter, repo, [
      new IncompleteSaga(_saga(_id(26), old), TENANT),
      new IncompleteSaga(_saga(_id(27), old), TENANT)], logger);

    var armed = await svc.ArmStrandedSagasAsync(new FixedWakes(new HashSet<Guid>()), CancellationToken.None);

    await Assert.That(armed).IsEqualTo(1)
      .Because("only the sweep's own token stops the sweep; a timeout inside one saga's read is that saga's failure");
    await Assert.That(logger.Entries.Single().Exception).IsTypeOf<OperationCanceledException>();
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

  private static void _clearAmbientScope() {
    Whizbang.Core.Security.ScopeContextAccessor.CurrentContext = null;
    Whizbang.Core.Security.ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  private sealed record ScopedRead(string Method, Guid SagaId, string? TenantId);

  /// <summary>
  /// Reads the way a repository over a tenant-scoped lens does: through the ambient scope accessor,
  /// throwing when the saga's tenant is not the one in force.
  /// </summary>
  private sealed class TenantScopedItemRepository(IReadOnlyDictionary<Guid, string?> tenantOf, DateTimeOffset lastActivity) : ISagaItemRepository {
    private readonly Whizbang.Core.Security.ScopeContextAccessor _accessor = new();
    public List<ScopedRead> Reads { get; } = [];

    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken) {
      _read(nameof(GetAggregateForSagaAsync), sagaId);
      return Task.FromResult(new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1));
    }

    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken) {
      _read(nameof(GetItemsAsync), sagaId);
      return Task.FromResult<IReadOnlyList<SagaItemModel>>([]);
    }

    public Task<DateTimeOffset?> GetLastActivityAsync(Guid sagaId, CancellationToken cancellationToken) {
      _read(nameof(GetLastActivityAsync), sagaId);
      return Task.FromResult<DateTimeOffset?>(lastActivity);
    }

    private void _read(string method, Guid sagaId) {
      var ambient = _accessor.Current?.Scope?.TenantId;
      var expected = tenantOf[sagaId];
      if (ambient != expected) {
        throw new InvalidOperationException(
          $"Scope 'Tenant' requires ambient scope context for tenant '{expected}' but found '{ambient ?? "none"}'.");
      }
      Reads.Add(new ScopedRead(method, sagaId, ambient));
    }
  }

  /// <summary>Idle items for every saga; throws, or cancels the sweep, when a chosen saga is read.</summary>
  private sealed class FailingItemRepository(DateTimeOffset lastActivity) : ISagaItemRepository {
    public Guid? FailFor { get; init; }
    public Exception FailWith { get; init; } = new InvalidOperationException("read failed");
    public Guid? CancelAt { get; init; }
    public CancellationTokenSource? Cancellation { get; init; }
    public List<Guid> ReadSagas { get; } = [];

    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1));

    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<IReadOnlyList<SagaItemModel>>([]);

    public async Task<DateTimeOffset?> GetLastActivityAsync(Guid sagaId, CancellationToken cancellationToken) {
      ReadSagas.Add(sagaId);
      if (sagaId == CancelAt) {
        await Cancellation!.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
      }
      if (sagaId == FailFor) {
        throw FailWith;
      }
      return lastActivity;
    }
  }

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class RecordingLogger : ILogger {
    public List<LogEntry> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
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

  /// <summary>A controllable clock for the sweep's idle and re-arm arithmetic.</summary>
  private sealed class MovableClock(DateTimeOffset start) : TimeProvider {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  /// <summary>Claims keys the way the claim store does: the first caller of a key wins, forever.</summary>
  private sealed class ClaimingEmitter : ISagaEventEmitter {
    private readonly HashSet<string> _claimed = [];
    public List<IEvent> Published { get; } = [];
    /// <summary>What was published without a claim.</summary>
    public List<IEvent> Plain { get; } = [];
    public void Claim(string claimKey) => _claimed.Add(claimKey);
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Plain.Add(eventData);
      return Task.CompletedTask;
    }
    public Task<IReadOnlySet<string>> FindClaimedAsync(IReadOnlyCollection<string> claimKeys, CancellationToken cancellationToken)
      => Task.FromResult<IReadOnlySet<string>>(claimKeys.Where(_claimed.Contains).ToHashSet());
    public Task<bool> ReleaseClaimAsync(string claimKey, CancellationToken cancellationToken)
      => Task.FromResult(_claimed.Remove(claimKey));
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent
      => PublishOnceInTenantAsync(null, claimKey, eventData, cancellationToken);
    public Task<bool> PublishOnceInTenantAsync<TEvent>(string? tenantId, string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      if (!_claimed.Add(claimKey)) {
        return Task.FromResult(false);
      }
      Published.Add(eventData);
      return Task.FromResult(true);
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
      IReadOnlyList<IncompleteSaga>? incomplete,
      ILogger? logger = null,
      SagaOptions? options = null)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, itemRepository, new NotTerminalReader(), options, logger ?? NullLogger<SweptSagaService>.Instance) {

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
