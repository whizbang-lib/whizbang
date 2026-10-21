# Saga commit / rollback signal

**Status:** in progress (branch `feat/saga-commit-rollback`)
**Docs:** `fundamentals/sagas/commit-rollback` (docs repo, new page)
**Tracking:** #1613

## The problem

A saga that runs a chain of steps can fail partway and leave the effects of its earlier steps in
place. An early step switches which version of some configuration is active; a later step fails;
the saga ends `Failed` with the switch still applied, and nothing in the framework tells anyone to
undo it. A consumer-side cancel that only stops new items from dispatching undoes nothing either.

The framework already knows how every saga ends, through four paths: the per-item fast path, the
watchdog's recovery path, a consumer's explicit `CompleteSagaAsync(Failed)` (fail-fast), and the
watchdog's abandonment. What it does not do is turn that ending into one decision a consumer can act
on. `ISagaCompensatingEvent` marks compensation events but nothing ever asks for compensation.

## The design

A saga that opts in gets exactly one terminal **outcome signal**, commit or rollback, published once
per saga whichever path ends it.

| Final status | Outcome |
|---|---|
| `Completed` | Commit |
| `CompletedWithFailures` | Rollback by default; per saga, `OnCompletedWithFailures = SagaOutcome.Commit` |
| `Failed` | Rollback |
| `Abandoned` | Rollback |
| Explicit `RollbackSagaAsync` (a consumer's cancel) | Rollback |

The framework signals; it does not compensate. Compensation order is domain-specific, which is the
reason `ISagaCompensatingEvent` gave for not shipping an engine, and that reason still holds. What
changes is that the decision now has a single, durable, exactly-once source.

### Surface

- `[SignalsOutcome]` on a `[Saga]` class opts it in. Property `OnCompletedWithFailures`
  (`SagaOutcome`, default `Rollback`). The generator emits the registration into the same module
  initializer the continuation registrations use, so it works with `GenerateService = false` and
  across assemblies.
- `SagaOutcome { Commit, Rollback }` and `SagaOutcomePolicy` (the status-to-outcome mapping, the one
  place the table above lives).
- `SagaOutcomeRegistry`: saga name to policy, composed per assembly, idempotent, like
  `SagaContinuationRegistry`.
- Concrete framework events `SagaCommittedEvent` and `SagaRolledBackEvent`, both on the saga's own
  stream, both `ISagaOutcomeEvent` (`SagaName`, `EntityId`, `FinalStatus`, `Reason`). Concrete for
  the same reason `SagaContinuationRequestedEvent` is: the framework constructs them with no
  per-saga subclass, and a receptor filters on `SagaName`.
- `BaseSagaService.RollbackSagaAsync(ctx, reason, ct)`: the explicit rollback a cancel calls. It
  signals rollback even for a saga that did not opt in, because the caller asked for it by name.
- `BaseSagaModel.Outcome` plus `SagaApplyHelper.TrackOutcome`, so a projection that applies the
  outcome events shows committed / rolled back.

### Exactly once, one outcome

One claim key per saga, `saga-outcome:{sagaName}:{sagaId}`, shared by commit and rollback. A saga
therefore gets one outcome, never both, whichever path asks first: a rollback taken by a cancel is
not overturned by a late completion, and a commit is not overturned by a later abandonment.

Separate from the completion claim, for the reason continuations have their own: every caller that
reaches terminal attempts the signal, including the ones that lost the completion claim, and the
claim collapses them to one emission. A watchdog tick that finds the saga already complete attempts
it again from the projection's recorded status, which covers a process that died between the
completion and the signal.

A failed signal publish does not fail the completion (same rule as continuations); it is logged and
the next watchdog tick retries. At abandonment it does propagate, so the tick is retried and the
rollback is not lost with no tick left to retry it.

### Stage, then apply

The guidance the docs page gives implementations, which is what makes a saga behave
transactionally across services:

1. Steps **stage** their effects (write the new configuration version as inactive, reserve rather
   than charge, write to a pending table keyed by saga id).
2. The **commit** receptor applies what was staged (activate the version, capture the reservation).
3. The **rollback** receptor discards what was staged, and compensates for anything a step could not
   stage and had to apply directly.

A step that stages has nothing to undo on rollback beyond deleting its staging, which is why the
shape is encouraged: compensation for an applied effect is the hard, domain-specific case, and
staging removes most of it.

## Undecided (filed as `question` issues)

1. (#1614) Default outcome for `CompletedWithFailures`: rollback (chosen, "a failure should roll back") or
   commit (matches the continuation default, which treats a partial run as having produced state).
2. (#1615) A first-class Cancel in W!: today a cancel is the consumer's; `RollbackSagaAsync` gives it the
   signal but does not stop dispatch or record a status. Should the framework own Cancel?
3. (#1616) Re-driving a saga after its rollback was signaled: the outcome claim stays taken, so a re-driven
   saga that then completes publishes no commit. Refuse the re-drive, or release the claim?
4. (#1617) Should the rollback signal carry a manifest of what ran (completed hooks and items, in reverse
   order) so a compensator does not have to read the projection?

## Out of scope

- An automated compensation engine.
- Opting in by default for every saga (an extra event per saga run for consumers that never asked).

## Tasks

- [x] Docs page (docs repo) and the compensation section of `whizbang-sagas` updated
- [x] Tests first: policy mapping, registry, generator registration, each emission path, one-outcome
      claim, explicit rollback, already-complete retry, model apply
- [x] Implementation
- [x] `dotnet format`, build + Sagas tests under the shared build lock
- [ ] PR (`Fixes` the tracking issue), ADO item, board card
