# An idle band, and two independent drain options

## Why

Audit is declared `BACKGROUND` and shares that bucket with bulk import traffic. On a deployed
service during an import, `wh_per_audit_event` took **17,445 writes — 22.4% of every read-model
write on the gateway service** — and `AuditProjection` was the deepest queue on it (763 pending).
Nobody waits for an audit row. Someone is waiting for the import.

The band comment already admits the conflation: band 200+ is *"work that exists because of volume
**or maintenance**"*. Two intents, one bucket, and the busier one is the one nobody needs promptly.

`SystemEventEmitter`, `AuditingEventStoreDecorator`, `AuditOutboxMessageBuilder` and
`RepairDrainWorker` all declare `BACKGROUND` today and are candidates for the same treatment.

## What this is not

Not a replacement for the housekeeping gate. Periodic maintenance is a timer-driven sweep with no
row in any queue, scheduled by `HousekeepingCoordinator` on a settled dwell with a bounded deferral
budget. Priority cannot express "wait until the service has been quiet for two minutes"; the gate
can, and it works -- after 165 the whole maintenance cycle costs 162ms and only runs when idle.
Maintenance stays where it is. What changes is that it should also wait behind **idle-band work**,
so the quiet window drains real work before housekeeping consumes it.

## The design

Two options, independent, per message class:

| trickle | idle-drain | behaviour |
|---|---|---|
| — | — | ordinary band ordering; unchanged |
| — | yes | runs only while the service is settled |
| yes | — | always progresses slowly, never bursts |
| yes | yes | trickles so it cannot starve, bursts when quiet |

Three time bounds, all configurable:

| bound | default | meaning |
|---|---|---|
| `TrickleAfter` | 30 min | a row older than this may be claimed even while busy |
| `TrickleSlice` | small | how many such rows one claim may take |
| `ForceFullDrainAfter` | 4 h | past this, the band drains at full width regardless of activity |

`ForceFullDrainAfter` is the floor that makes the whole thing safe. A service that never goes quiet
still empties the band, at a bounded interval, and the forced pass is reported distinctly -- the
same shape as the housekeeping deferral budget, and the same reason: a gate with no floor is a way
to lose data silently. Audit that never runs is audit that is gone.

Durations, not counts. `MaxConsecutiveDeferrals` is cadence-coupled -- its meaning changes when the
poll interval moves -- and that is the defect this avoids repeating.

## It costs no new index on the hot table, and that is the point

The claim table carries **11 indexes against a gated ceiling of 12**. The baseline says why:
*"the write win IS the index count on the hot table; 6 indexes gave 69 percent off, 11 gives 51, and
a return toward 17 gives back the whole reason the split exists."* One slot remains and this feature
must not spend it.

It does not have to. Measured on a deployed service:

```
SET enable_seqscan = off;
EXPLAIN SELECT ... FROM wh_inbox_state
WHERE processed_at IS NULL AND is_event = true AND priority >= 400
  AND received_at < now() - interval '30 minutes'
ORDER BY received_at, message_id LIMIT 100;

Index Scan using idx_inbox_state_pending_arrival_background
  Index Cond: (received_at < (now() - '00:30:00'::interval))
  Filter: (priority >= 400)
```

Two things fall out:

1. **The idle band nests inside the background lane.** The planner proves `priority >= 400` implies
   the lane's `priority > 199`, because both are constants. This is the opposite of the defect in
   the lane-selection fix, where the bound came from a joined column and no proof was possible --
   so the rule stands: **the band bound must be a literal in the claim's SQL, never a computed or
   joined value.** A guard test already exists for that shape and must cover the new branch.
2. **The trickle age test is the leading key.** The lane is keyed `(received_at, message_id)`, so
   `received_at < now() - interval` is an Index Cond, not a filter. Trickle needs no index of its
   own.

Net: **zero new indexes on `wh_inbox_state`.** The gate stays at 11/12.

## Where the flags live

Per **class**, not per message, unless a case appears that needs otherwise.

`PriorityOptions` already configures by namespace, type and rule; `TagOptions.DeclarePriority` does
it by tag. Adding the two booleans there costs no column, no migration, and no row storage, and the
claim derives the behaviour from the band bound alone.

Per-message bits are the alternative and are more expensive than they look. `wh_inbox` and
`wh_outbox` carry a `flags` integer with bits 1, 4 and 8 used, so two bits are free -- but **the
claim reads `wh_inbox_state`, which has no `flags` column**, and neither does
`wh_perspective_events`. Reaching the bits would mean either copying them onto the state row at
store time (as `priority` already is) or joining back to the wide row, which is precisely what the
state-table split removed. Do not join back.

## What must not regress

The cost suite already gates the properties this feature could damage. Any change here must leave
these intact and should extend them:

| measure | ceiling | why it matters here |
|---|---|---|
| `claim_write.state_row.indexes` | 12 | a new lane index would spend the last slot |
| `claim.blocks_per_call.depth_*` | 8000 | a fourth UNION branch costs a claim something |
| `claim.rows_leased_per_call.*` | 50 | the trickle slice must not widen the normal claim |
| `probe.*.seq_scans_per_call.*` | 0.5 | a band whose predicate no index serves would show here |

New measures to record, gated only where the number is a property of the design:

- `idle_band.claim.blocks_per_call.busy` -- what a claim costs while the band is withheld
- `idle_band.claim.blocks_per_call.settled` -- and while it drains
- `idle_band.seq_scans_per_call` -- **gated at 0.5**; the band must be served by the existing lane
- `idle_band.rows_leased_per_call.trickle` -- **gated**; the slice is the point
- `idle_band.oldest_row_age_at_forced_drain` -- recorded; proves the 4h floor fires

## Tests

1. **Usability, EXPLAIN-based**, extending `PriorityLaneIndexUsabilityTests`: the idle branch is
   served by the background lane and takes no sequential scan. This is the case that would have
   caught the original lane defect and must cover the new bound.
2. **Literal-bound guard**, extending the claim-shape rule: the idle bound is a literal, never a
   joined or computed value.
3. **Behaviour**: withheld while busy; drains when settled; a row older than `TrickleAfter` is taken
   while busy but only up to `TrickleSlice`; the band drains fully past `ForceFullDrainAfter` even
   with no quiet window -- that last one is the floor and must fail loudly if removed.
4. **Cost scenario** in the performance suite, both sides measured against one fixture, in the
   established shape: work done, never wall clock.
5. **Maintenance sequencing**: `ServiceBacklog` gains an idle-band count and the housekeeping gate
   requires it to be zero, so maintenance follows idle work rather than competing with it.

## Sequencing

**The outbox does not honour the three bands it already has.** Its drain orders by
`es.commit_sequence, message_id` and it carries no priority index, while the inbox has six. A chat
reply therefore queues behind import traffic no matter what band it is in -- which is the symptom
that started this. Fix that first: the API, the classification, the column and the wire format are
all in place, so it is an `ORDER BY` and a lane index on a table whose write cost is not gated.

Build the idle band second, on a chain that is demonstrably honouring priority end to end.
