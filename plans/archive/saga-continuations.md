> **Archived 2026-09-26.** The work this plan describes has shipped, or its open remainder is carried by a card on the Release 1.0 or 2.0 Planning board; the Done cards carry the Shipped dates. Status lines below are as last written and may be stale.

# Saga continuations: running one saga after another finishes

## The problem

Two workloads that belong to the same operation compete for the same claim capacity
because nothing sequences them. A bulk import produces a large volume of events; a
derived enrichment pass over the same rows is queued while that import is still
running. The enrichment reads the rows the import is still writing, so it competes
for claim capacity, for the database, and it reads data whose indexes the perspective
worker has not finished building. None of that work needed to happen concurrently.

## Why priority cannot fix it

The instinct is to declare the enrichment lower than background so it waits. Measured
against the claim, that does not work, for three separate reasons.

**Within a bucket, the number does not order anything.** `150_BucketAwareClaim.sql`
selects in three lanes by bucket (interactive 1-99, standard 100-199, background 200
and up) and orders inside each lane by `received_at, message_id`. A declaration of 400
and a declaration of 250 both land in the background lane and are then ordered by
arrival. A new constant below `BACKGROUND` would change no scheduling decision.

**Background holds a guaranteed floor.** `c_background_floor_share` is `0.1`, so the
background lane receives at least a tenth of every batch while it has pending streams.
Enrichment queued at background priority therefore takes claim capacity away from the
import for as long as both are pending, which is the contention being complained about.

**Background is promoted with age, not demoted.** `c_background_wait_target_seconds`
is `300`, and a background row older than that competes in the standard lane. So the
longer the import runs, the more urgent the waiting enrichment becomes. The mechanism
does the opposite of waiting.

The conclusion is that priority expresses "how urgent is this relative to other work
in flight", and the requirement here is "this work must not be in flight yet". Those
are different questions and only the second one removes the contention.

## What this adds

A saga declares that another saga follows it. When the first saga reaches a terminal
status, the framework asks for the second one to start. The enrichment is then not
queued at all while the import runs, so there is no capacity to contend for, and the
rows it reads are the rows the import finished writing.

Once sequenced, the priority question answers itself: the enrichment runs when the
import is over, so `BACKGROUND` is both correct and sufficient, and neither the floor
nor the age promotion matters because there is nothing left to compete with.

### Surface

`[ContinuesWith("OtherSaga")]` on a saga class, repeatable. A trigger says which final
statuses start the continuation, defaulting to "ran to the end" (`Completed` or
`CompletedWithFailures`) because a partial import still has rows worth enriching, while
an aborted one does not.

The declaration reaches runtime through `SagaContinuationRegistry`, which each assembly
writes into from its generated registration. A generator sees only its own compilation,
so the saga and the host that composes it can be in different assemblies; the registry
composes as assemblies load, with no reflection, the same shape the index, message-type,
and query-exposure registries already use.

`SagaContinuationRequestedEvent` is the framework-owned event carrying the request,
following `SagaCompletionAbandonedEvent`: concrete, so the framework constructs it
without a per-saga subclass, and shape-compatible with the rest of the lifecycle so
existing registry routing handles it. Its `SagaName` is the saga being asked to start,
which is what lets a consumer's receptor filter on the name it already knows.

### Exactly once, and not lost

The continuation is published through `PublishOnceAsync` under its own claim key,
`saga-continuation:{parent}:{parentSagaId}:{continuation}`, separate from the
completion claim.

Separate on purpose. The obvious placement is "publish the continuation when this
caller wins the completion claim", but then a process that dies between winning the
completion claim and publishing the continuation loses the continuation permanently:
the completion claim is already taken, so no retry and no watchdog tick will drive it
again. Giving the continuation its own key lets every caller that reaches terminal
attempt it, including the ones that lost the completion claim and including the
watchdog recovery path, while the claim still collapses them to one emission.

## Not included

A scheduling class genuinely below background, exempt from the floor and from the age
promotion, is a real thing the claim could express and is not what this change does.
It would be a new lane, a new partial index, and a migration, and sequencing removes
the need for it in the case that motivated this.

## Consumer side

The host writes a receptor on `SagaContinuationRequestedEvent`, filters on the saga
name it declared, and calls `InitiateSagaAsync` for that saga with the parent's
`EntityId`. The framework does not initiate the child itself: it does not know the
child's item set, and inventing one would be guessing at consumer domain.
