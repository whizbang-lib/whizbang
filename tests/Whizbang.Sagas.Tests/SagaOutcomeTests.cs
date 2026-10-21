// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Sagas;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A saga that signals its outcome is committed or rolled back exactly once, whichever way it ends.
/// </summary>
/// <remarks>
/// <para>
/// The case that motivated it: an early step switches which version of something is active, a later
/// step fails, and the saga ends failed with the switch still in place. The framework knows the run
/// failed; without a signal, nothing tells the consumer to undo the switch.
/// </para>
/// <para>
/// The parts worth pinning are the ones a consumer cannot recover from if they are wrong: a run that
/// gets both outcomes, a run that gets none because the process died between the completion and the
/// signal, and an abandoned run that is never rolled back because no watchdog tick is left to retry.
/// </para>
/// <para>
/// Each test registers its own saga name, because the registry is static and a name shared across
/// tests would let one test's declaration change another's outcome.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback</docs>
[Category("Unit")]
[Category("Saga")]
public class SagaOutcomeTests {
  private static readonly Guid _sagaId = Guid.NewGuid();
  private static readonly Guid _entityId = Guid.NewGuid();

  // ── The policy ───────────────────────────────────────────────────────

  /// <summary>A run that succeeded commits; one that stopped partway rolls back.</summary>
  [Test]
  [Arguments(SagaStatus.Completed, SagaOutcome.Commit)]
  [Arguments(SagaStatus.Failed, SagaOutcome.Rollback)]
  [Arguments(SagaStatus.Abandoned, SagaOutcome.Rollback)]
  public async Task EveryTerminalStatusHasAnOutcomeAsync(SagaStatus status, SagaOutcome expected) {
    await Assert.That(SagaOutcomePolicy.Default.OutcomeFor(status)).IsEqualTo(expected);
  }

  /// <summary>Some items failing rolls the run back unless the saga said otherwise.</summary>
  /// <remarks>
  /// A saga that opted in to an outcome is one whose steps must not outlive a failed run, so a
  /// partial failure is a failure. The opposite default would commit half a run for every saga that
  /// declared nothing beyond <c>[SignalsOutcome]</c>.
  /// </remarks>
  [Test]
  public async Task CompletedWithFailuresRollsBackByDefaultAsync() {
    await Assert.That(SagaOutcomePolicy.Default.OutcomeFor(SagaStatus.CompletedWithFailures))
      .IsEqualTo(SagaOutcome.Rollback);
    await Assert.That(new SignalsOutcomeAttribute().OnCompletedWithFailures).IsEqualTo(SagaOutcome.Rollback)
      .Because("the attribute's default and the policy's default have to agree, or a declaration "
        + "with no argument means something different from no declaration of the property");
  }

  /// <summary>A saga that tolerates gaps can keep a partly failed run.</summary>
  [Test]
  public async Task ASagaCanCommitAPartlyFailedRunAsync() {
    var tolerant = new SagaOutcomePolicy(onCompletedWithFailures: SagaOutcome.Commit);

    await Assert.That(tolerant.OutcomeFor(SagaStatus.CompletedWithFailures)).IsEqualTo(SagaOutcome.Commit);
    await Assert.That(tolerant.OutcomeFor(SagaStatus.Failed)).IsEqualTo(SagaOutcome.Rollback)
      .Because("tolerating failed items is not the same as tolerating a run that stopped partway");
  }

  /// <summary>A run that has not ended has no outcome.</summary>
  [Test]
  [Arguments(SagaStatus.Pending)]
  [Arguments(SagaStatus.Running)]
  [Arguments(SagaStatus.Reset)]
  public async Task ANonTerminalStatusHasNoOutcomeAsync(SagaStatus status) {
    await Assert.That(SagaOutcomePolicy.Default.OutcomeFor(status)).IsNull();
  }

  // ── The registry and the claim key ───────────────────────────────────

  /// <summary>A saga nobody opted in signals nothing.</summary>
  [Test]
  public async Task AnUndeclaredSagaHasNoPolicyAsync() {
    await Assert.That(SagaOutcomeRegistry.For("OutcomeTestsUndeclared")).IsNull();
  }

  /// <summary>The first declaration of a saga stands.</summary>
  /// <remarks>
  /// A library and the host composing it can both register one saga. Letting the second replace the
  /// first would give runs of one saga different outcomes for the same status, depending on which
  /// assembly loaded last.
  /// </remarks>
  [Test]
  public async Task TheFirstRegistrationStandsAsync() {
    const string NAME = "OutcomeTestsFirstStands";
    SagaOutcomeRegistry.Register(NAME, new SagaOutcomePolicy(SagaOutcome.Commit));
    SagaOutcomeRegistry.Register(NAME, new SagaOutcomePolicy(SagaOutcome.Rollback));

    await Assert.That(SagaOutcomeRegistry.For(NAME)!.OnCompletedWithFailures).IsEqualTo(SagaOutcome.Commit);
  }

  /// <summary>A blank name or a missing policy is a declaration error, caught where it is made.</summary>
  [Test]
  public async Task ABadRegistrationIsRejectedAsync() {
    await Assert.That(() => SagaOutcomeRegistry.Register("", SagaOutcomePolicy.Default)).Throws<ArgumentException>();
    await Assert.That(() => SagaOutcomeRegistry.Register("OutcomeTestsNull", null!)).Throws<ArgumentNullException>();
    await Assert.That(() => SagaOutcomeRegistry.For(" ")).Throws<ArgumentException>();
    await Assert.That(() => SagaOutcomeGuard.ClaimKey("", _sagaId)).Throws<ArgumentException>();
  }

  /// <summary>
  /// The outcome has its own claim, one claim for both outcomes.
  /// </summary>
  /// <remarks>
  /// Sharing the completion claim would mean only the completion winner signals, and the signal is
  /// lost if that process dies before publishing it. Two claims, one per outcome, would let a cancel
  /// roll a run back and a completion already in flight commit it afterwards.
  /// </remarks>
  [Test]
  public async Task TheOutcomeClaimKeyIsItsOwnAndSharedByBothOutcomesAsync() {
    var key = SagaOutcomeGuard.ClaimKey("OutcomeTestsKey", _sagaId);

    await Assert.That(key).IsEqualTo($"saga-outcome:OutcomeTestsKey:{_sagaId}");
    await Assert.That(key).IsNotEqualTo(SagaCompletionGuard.ClaimKey("OutcomeTestsKey", _sagaId));
    await Assert.That(key).IsNotEqualTo(SagaAbandonGuard.ClaimKey("OutcomeTestsKey", _sagaId));
  }

  // ── Completion signals the outcome ───────────────────────────────────

  /// <summary>A run that succeeded is committed, once, on its own stream.</summary>
  [Test]
  public async Task CompletingAnOptedInSagaCommitsItAsync() {
    const string NAME = "OutcomeTestsCommits";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService(NAME, emitter);

    await service.CompleteSagaAsync(_ctx(), SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    var committed = emitter.Published.OfType<SagaCommittedEvent>().Single();
    await Assert.That(committed.SagaName).IsEqualTo(NAME);
    await Assert.That(committed.StreamId).IsEqualTo(_sagaId);
    await Assert.That(committed.EntityId).IsEqualTo(_entityId);
    await Assert.That(committed.Outcome).IsEqualTo(SagaOutcome.Commit);
    await Assert.That(committed.FinalStatus).IsEqualTo(SagaStatus.Completed);
    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>()).IsEmpty();
    await Assert.That(emitter.ClaimsAttempted).Contains(SagaOutcomeGuard.ClaimKey(NAME, _sagaId));
  }

  /// <summary>
  /// The motivating case: a run that failed partway is rolled back, so its earlier steps can be undone.
  /// </summary>
  [Test]
  public async Task AFailedRunIsRolledBackAsync() {
    const string NAME = "OutcomeTestsFailed";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService(NAME, emitter);

    await service.CompleteSagaAsync(_ctx(), SagaStatus.Failed, "step-2", 1, 1, 3, CancellationToken.None);

    var rolledBack = emitter.Published.OfType<SagaRolledBackEvent>().Single();
    await Assert.That(rolledBack.Outcome).IsEqualTo(SagaOutcome.Rollback);
    await Assert.That(rolledBack.FinalStatus).IsEqualTo(SagaStatus.Failed);
    await Assert.That(rolledBack.Reason).IsNotNull()
      .Because("the compensator should be able to log why it is undoing work without reading the projection");
    await Assert.That(emitter.Published.OfType<SagaCommittedEvent>()).IsEmpty();
  }

  /// <summary>The saga's own policy decides a partly failed run.</summary>
  [Test]
  [Arguments(SagaOutcome.Rollback)]
  [Arguments(SagaOutcome.Commit)]
  public async Task APartlyFailedRunFollowsTheSagasPolicyAsync(SagaOutcome onCompletedWithFailures) {
    var name = $"OutcomeTestsPartial{onCompletedWithFailures}";
    SagaOutcomeRegistry.Register(name, new SagaOutcomePolicy(onCompletedWithFailures));
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService(name, emitter);

    await service.CompleteSagaAsync(_ctx(), SagaStatus.CompletedWithFailures, "item-3", 2, 1, 3, CancellationToken.None);

    var signaled = emitter.Published.OfType<ISagaOutcomeEvent>().Single();
    await Assert.That(signaled.Outcome).IsEqualTo(onCompletedWithFailures);
    await Assert.That(signaled.FinalStatus).IsEqualTo(SagaStatus.CompletedWithFailures);
  }

  /// <summary>A saga that did not opt in completes exactly as it did before.</summary>
  [Test]
  public async Task ASagaThatDidNotOptInSignalsNothingAsync() {
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService("OutcomeTestsNotOptedIn", emitter);

    await service.CompleteSagaAsync(_ctx(), SagaStatus.Failed, "item-1", 0, 1, 1, CancellationToken.None);

    await Assert.That(emitter.Published.OfType<ISagaOutcomeEvent>()).IsEmpty();
    await Assert.That(emitter.ClaimsAttempted.Count).IsEqualTo(1)
      .Because("only the completion claim is taken; an extra claim per run would cost every saga that never asked");
  }

  /// <summary>
  /// A caller that loses the completion claim still signals the outcome.
  /// </summary>
  /// <remarks>
  /// The loss window the separate claim closes: if only the completion winner signaled, a process
  /// dying between the two publishes would leave the run with no outcome, and its earlier steps'
  /// effects would never be undone.
  /// </remarks>
  [Test]
  public async Task LosingTheCompletionClaimStillSignalsTheOutcomeAsync() {
    const string NAME = "OutcomeTestsLostCompletion";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    emitter.Claimed.Add(SagaCompletionGuard.ClaimKey(NAME, _sagaId));
    var service = new OutcomeTestSagaService(NAME, emitter);

    var won = await service.CompleteSagaAsync(_ctx(), SagaStatus.Failed, "item-1", 0, 1, 1, CancellationToken.None);

    await Assert.That(won).IsFalse();
    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>().Count()).IsEqualTo(1);
  }

  /// <summary>A signal that fails to publish does not fail the completion.</summary>
  /// <remarks>
  /// The run did finish and its completion event records it. Propagating would roll back the caller's
  /// transaction and re-run the terminal path; the watchdog tick that finds the run complete retries
  /// the signal instead (see <see cref="AWatchdogTickOnACompletedRunRetriesTheSignalAsync"/>).
  /// </remarks>
  [Test]
  public async Task AFailedSignalDoesNotFailTheCompletionAsync() {
    const string NAME = "OutcomeTestsSignalThrows";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter { ThrowOnOutcome = true };
    var service = new OutcomeTestSagaService(NAME, emitter);

    var won = await service.CompleteSagaAsync(_ctx(), SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    await Assert.That(won).IsTrue();
  }

  // ── Explicit rollback ────────────────────────────────────────────────

  /// <summary>A cancel rolls the run back, with the caller's reason, before it reaches a status.</summary>
  [Test]
  public async Task AnExplicitRollbackSignalsRollbackWithTheReasonAsync() {
    const string NAME = "OutcomeTestsExplicit";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService(NAME, emitter);

    var won = await service.RollbackSagaAsync(_ctx(), "canceled by an operator", CancellationToken.None);

    await Assert.That(won).IsTrue();
    var rolledBack = emitter.Published.OfType<SagaRolledBackEvent>().Single();
    await Assert.That(rolledBack.Reason).IsEqualTo("canceled by an operator");
    await Assert.That(rolledBack.FinalStatus).IsNull()
      .Because("the run had not ended; a status here would claim it had");
    await Assert.That(rolledBack.StreamId).IsEqualTo(_sagaId);
  }

  /// <summary>An explicit rollback is honored for a saga that did not opt in.</summary>
  /// <remarks>
  /// Opting in decides whether completion signals on its own. A caller asking for a rollback by name
  /// has made the decision already, and silently doing nothing would leave it believing it had.
  /// </remarks>
  [Test]
  public async Task AnExplicitRollbackWorksWithoutOptingInAsync() {
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService("OutcomeTestsExplicitNotOptedIn", emitter);

    await service.RollbackSagaAsync(_ctx(), "canceled", CancellationToken.None);

    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>().Count()).IsEqualTo(1);
  }

  /// <summary>A blank reason is refused: a rollback nobody can explain is one nobody can audit.</summary>
  [Test]
  public async Task AnExplicitRollbackNeedsAReasonAsync() {
    var service = new OutcomeTestSagaService("OutcomeTestsNoReason", new ClaimingEmitter());

    await Assert.That(async () => await service.RollbackSagaAsync(_ctx(), " ", CancellationToken.None))
      .Throws<ArgumentException>();
  }

  /// <summary>
  /// A run rolled back by a cancel is not committed by a completion that was already in flight.
  /// </summary>
  [Test]
  public async Task ARolledBackRunIsNeverCommittedAsync() {
    const string NAME = "OutcomeTestsCancelThenComplete";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService(NAME, emitter);

    await service.RollbackSagaAsync(_ctx(), "canceled", CancellationToken.None);
    await service.CompleteSagaAsync(_ctx(), SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>().Count()).IsEqualTo(1);
    await Assert.That(emitter.Published.OfType<SagaCommittedEvent>()).IsEmpty()
      .Because("both outcomes share one claim, so the first asked for is the outcome");
  }

  /// <summary>A second explicit rollback reports that it was not the one that signaled.</summary>
  [Test]
  public async Task ASecondRollbackIsNotSignaledAgainAsync() {
    var emitter = new ClaimingEmitter();
    var service = new OutcomeTestSagaService("OutcomeTestsTwice", emitter);

    await service.RollbackSagaAsync(_ctx(), "first", CancellationToken.None);
    var second = await service.RollbackSagaAsync(_ctx(), "second", CancellationToken.None);

    await Assert.That(second).IsFalse();
    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>().Single().Reason).IsEqualTo("first");
  }

  // ── The watchdog ─────────────────────────────────────────────────────

  /// <summary>An abandoned run is rolled back.</summary>
  /// <remarks>
  /// Abandonment is the path a stuck run takes, and it is the one no completion call covers: the
  /// watchdog stops waiting, publishes the abandonment, and nothing else ever ends the run.
  /// </remarks>
  [Test]
  public async Task AnAbandonedRunIsRolledBackAsync() {
    const string NAME = "OutcomeTestsAbandoned";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var service = _stalledService(NAME, emitter);

    var outcome = await service.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(NAME), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Abandoned);
    var rolledBack = emitter.Published.OfType<SagaRolledBackEvent>().Single();
    await Assert.That(rolledBack.FinalStatus).IsEqualTo(SagaStatus.Abandoned);
    await Assert.That(rolledBack.StreamId).IsEqualTo(_sagaId);
  }

  /// <summary>
  /// A tick that already abandoned the run still rolls it back on a retry.
  /// </summary>
  /// <remarks>
  /// A tick that won the abandonment claim and died before the rollback leaves a retried tick that
  /// loses the abandonment claim. The rollback must not be tied to winning it.
  /// </remarks>
  [Test]
  public async Task ARetriedAbandonmentStillRollsBackAsync() {
    const string NAME = "OutcomeTestsAbandonRetried";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    emitter.Claimed.Add(SagaAbandonGuard.ClaimKey(NAME, _sagaId));
    var service = _stalledService(NAME, emitter);

    await service.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(NAME), CancellationToken.None);

    await Assert.That(emitter.Published.OfType<SagaRolledBackEvent>().Count()).IsEqualTo(1);
  }

  /// <summary>
  /// At abandonment a failed signal propagates, so the tick is retried rather than the rollback lost.
  /// </summary>
  /// <remarks>
  /// Unlike completion, no later tick exists to retry it: the abandoning tick is the last in its chain.
  /// </remarks>
  [Test]
  public async Task AFailedRollbackAtAbandonmentPropagatesAsync() {
    const string NAME = "OutcomeTestsAbandonThrows";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var service = _stalledService(NAME, new ClaimingEmitter { ThrowOnOutcome = true });

    await Assert.That(async () => await service.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(NAME), CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  /// <summary>
  /// A tick that finds the run already complete signals its outcome again, from the recorded status.
  /// </summary>
  /// <remarks>
  /// Covers the process that won the completion and died before the signal, and the signal whose
  /// publish failed and was swallowed so the completion stood. The claim makes the retry free when the
  /// signal did land.
  /// </remarks>
  [Test]
  public async Task AWatchdogTickOnACompletedRunRetriesTheSignalAsync() {
    const string NAME = "OutcomeTestsAlreadyComplete";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var projection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = NAME,
      EntityId = _entityId,
      TotalItems = 3,
      Status = SagaStatus.Failed,
      CompletionEventDispatched = true,
    };
    var service = new OutcomeTestSagaService(NAME, emitter, projection: projection);

    var outcome = await service.TryRecoverViaWatchdogTickAsync(_tick(NAME), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.AlreadyComplete);
    var rolledBack = emitter.Published.OfType<SagaRolledBackEvent>().Single();
    await Assert.That(rolledBack.FinalStatus).IsEqualTo(SagaStatus.Failed);
  }

  /// <summary>A tick on a completed run of a saga that did not opt in signals nothing.</summary>
  [Test]
  public async Task AWatchdogTickOnACompletedRunThatDidNotOptInSignalsNothingAsync() {
    const string NAME = "OutcomeTestsAlreadyCompleteNotOptedIn";
    var emitter = new ClaimingEmitter();
    var projection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = NAME,
      EntityId = _entityId,
      TotalItems = 1,
      Status = SagaStatus.Completed,
      CompletionEventDispatched = true,
    };
    var service = new OutcomeTestSagaService(NAME, emitter, projection: projection);

    await service.TryRecoverViaWatchdogTickAsync(_tick(NAME), CancellationToken.None);

    await Assert.That(emitter.ClaimsAttempted).IsEmpty();
  }

  /// <summary>
  /// A run recorded as dispatched but not yet terminal on the projection has nothing to retry.
  /// </summary>
  /// <remarks>
  /// The completion claim was taken but the projection has not applied the completion yet, so the
  /// status that would decide the outcome is not known. Guessing would be worse than the next tick.
  /// </remarks>
  [Test]
  public async Task AWatchdogTickWithNoRecordedStatusSignalsNothingAsync() {
    const string NAME = "OutcomeTestsAlreadyCompleteNoStatus";
    SagaOutcomeRegistry.Register(NAME, SagaOutcomePolicy.Default);
    var emitter = new ClaimingEmitter();
    var projection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = NAME,
      EntityId = _entityId,
      TotalItems = 1,
      Status = SagaStatus.Running,
      CompletionEventDispatched = true,
    };
    var service = new OutcomeTestSagaService(NAME, emitter, projection: projection);

    await service.TryRecoverViaWatchdogTickAsync(_tick(NAME), CancellationToken.None);

    await Assert.That(emitter.Published.OfType<ISagaOutcomeEvent>()).IsEmpty();
  }

  // ── The projection ───────────────────────────────────────────────────

  /// <summary>A projection that applies the signal records it, and the first outcome stands.</summary>
  [Test]
  public async Task TheProjectionRecordsTheFirstOutcomeAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Failed };
    var at = DateTimeOffset.UtcNow;

    var first = SagaApplyHelper.TrackOutcome(saga, SagaOutcome.Rollback, at);
    var second = SagaApplyHelper.TrackOutcome(saga, SagaOutcome.Commit, at.AddSeconds(1));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("a replayed or duplicated event must not flip the recorded outcome");
    await Assert.That(saga.Outcome).IsEqualTo(SagaOutcome.Rollback);
    await Assert.That(saga.UpdatedAt).IsEqualTo(at);
  }

  /// <summary>A missing model is a wiring error.</summary>
  [Test]
  public async Task TrackingAnOutcomeNeedsAModelAsync() {
    await Assert.That(() => SagaApplyHelper.TrackOutcome(null!, SagaOutcome.Commit, DateTimeOffset.UtcNow))
      .Throws<ArgumentNullException>();
  }

  // ── Fixtures ─────────────────────────────────────────────────────────

  private static SagaContext _ctx() => new(_sagaId, _entityId);

  private static SagaCompletionWatchdogTickEvent _tick(string name) => new() {
    StreamId = _sagaId,
    SagaName = name,
    EntityId = _entityId,
    RescheduleCount = 0,
  };

  /// <summary>A tick that has seen no progress across the whole stall limit.</summary>
  private static SagaCompletionWatchdogTickEvent _tickAtTheStallLimit(string name) => new() {
    StreamId = _sagaId,
    SagaName = name,
    EntityId = _entityId,
    RescheduleCount = 4,
    LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
    LastObservedCompleted = 7,
    LastObservedFailed = 0,
    ConsecutiveStallCount = 3,
  };

  /// <summary>
  /// A run stuck at 7 of 10 with no item rows to resolve, so the stall-limit tick can only abandon it.
  /// </summary>
  private static OutcomeTestSagaService _stalledService(string name, ClaimingEmitter emitter) =>
    new(name, emitter,
      itemRepository: new FixedItemRepository(new SagaItemAggregate(Total: 10, Completed: 7, Failed: 0, InProgress: 3)),
      terminalReader: new NeverTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = name, EntityId = _entityId, TotalItems = 10 });

  /// <summary>
  /// An emitter whose claims behave like the dispatcher's: the first claim on a key wins, every later
  /// one loses, and only winners are published.
  /// </summary>
  private sealed class ClaimingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = [];
    public HashSet<string> Claimed { get; } = new(StringComparer.Ordinal);
    public List<string> ClaimsAttempted { get; } = [];
    public bool ThrowOnOutcome { get; init; }

    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.CompletedTask;
    }

    public Task<bool> PublishOnceAsync<TEvent>(
        string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      if (ThrowOnOutcome && eventData is ISagaOutcomeEvent) {
        throw new InvalidOperationException("transient publish failure");
      }

      ClaimsAttempted.Add(claimKey);
      if (!Claimed.Add(claimKey)) {
        return Task.FromResult(false);
      }

      Published.Add(eventData);
      return Task.FromResult(true);
    }
  }

  private sealed class FixedItemRepository(SagaItemAggregate agg) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(agg);
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<IReadOnlyList<SagaItemModel>>([]);
  }

  private sealed class NeverTerminalReader : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(SagaItemTerminalOutcome.NotTerminal);
  }

  private sealed class OutcomeInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
  }
  private sealed class OutcomeItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class OutcomeItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class OutcomeItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class OutcomeItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class OutcomeCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class OutcomeResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class OutcomeHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class OutcomeHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class OutcomeTestSagaService(
      string sagaName,
      ISagaEventEmitter emitter,
      ISagaItemRepository? itemRepository = null,
      ISagaItemTerminalReader? terminalReader = null,
      BaseSagaModel? projection = null)
    : BaseSagaService<OutcomeInitiatedEvent, OutcomeItemsDispatchedEvent, OutcomeItemStartedEvent,
                      OutcomeItemCompletedEvent, OutcomeItemFailedEvent, OutcomeCompletedEvent,
                      OutcomeResetEvent, OutcomeHookStartedEvent, OutcomeHookCompletedEvent>(
        sagaName, emitter, itemRepository, terminalReader, options: null, NullLogger<OutcomeTestSagaService>.Instance) {

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken) =>
      Task.FromResult(projection);

    protected override OutcomeInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override OutcomeItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override OutcomeItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override OutcomeItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override OutcomeItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override OutcomeCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override OutcomeResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override OutcomeHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override OutcomeHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
