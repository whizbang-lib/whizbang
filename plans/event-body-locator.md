# Event body locator: pointers in the event store

Part of [storage-reclamation.md](storage-reclamation.md). Builds on [storage-event-slimming.md](storage-event-slimming.md)
(superseding types, WS-2) and [storage-stream-end-of-life.md](storage-stream-end-of-life.md) (archive store and
read-through, WS-3). Absorbs #1975 (event-level archive). Release: 1.0. Tracking: #2431. Status: approved after owner review (tiers and integrity alignment, 2026-11-19).

## Context

### What superseding does today

A type marked `[Superseding]` holds the whole state for its key (`src/Whizbang.Core/Attributes/SupersedingAttribute.cs`).
When a newer event of the same stream, group and key is stored:

1. The newer event is appended at the end of the stream like any other event.
2. The older copy is queued in `wh_superseded_events`.
3. `wh_reap_superseded_events` (latest definition in migration 466) deletes the older copy, body and row, once every
   perspective that consumes it has processed it, no cursor or snapshot rests on it, no destruction hold is active and
   the commit-sequence stamper has reached it.
4. The digest counts the key's slot, not each copy (#2364), and the surviving copy inherits the victim's anchor
   (`_wh_hand_on_slots`), so integrity sees no change.

The stream is left with a hole where the older event was. A replay, a rebuild or an "as of" read between the two
events sees the value before the older event, then nothing, then the newer value at its own later position. Version
numbers have gaps. A causation chain that names the older event names nothing. In an ordered event store this is the
most dangerous way to save space: history that something may depend on is removed, not described.

### What the space actually is (development environment of the evidence deployment, 2026-11-19)

| Measure | Value |
| --- | --- |
| Superseding types in use | one pair (embedding created and updated), one service |
| Events of those types | 128,088 created, 739 updated |
| Average stored body | about 24.6 KB, nearly all of it the embedding vector |
| Copies ever superseded | under 1% of the created events |
| Queued copies held, not reaped | 104, every one held by a perspective cursor that names the older copy although the perspective applied the newer one (separate defect, see Prerequisites) |
| Exact duplicate bodies (busiest service) | about 0.6% |
| Repeatedly re-materialized type with small changes | a permissions snapshot type, about 1.2 GB in the busiest service |

Two conclusions shape this plan:

- **Choose for correctness first.** Superseding saves little space in the evidence deployment today under any layout;
  what it costs is history.
- **A diff saves space only when a change is small relative to the body.** An embedding update replaces the whole
  vector, so a diff is the whole body. A permissions snapshot changes a few entries, so a diff is small.

### What already exists to build on

| Part | Where | State |
| --- | --- | --- |
| Archive store | `IArchiveStore`, Postgres table store and Azure Blob store (WS-3 S7, S8) | ships; whole stream or prefix only |
| Read-through | `ArchiveAwareEventStoreDecorator` (WS-3 S9) | ships; fills a gap at the start of a stream |
| Event-level archive | #1975 | decided 2026-11-02 (body archived, row kept hot and flagged, read paths fill the body in place, fetched only when needed); moved to post-1.0 on 2026-11-03 |
| Dispositions | `Disposition` (Delete, Archive, Compact, CryptoShred) | Delete, Archive (stream level) and Compact wired; CryptoShred refused everywhere |
| Event flags | `EventFlags` | bits 0 to 7 used (`Superseding` is bit 7) |

## Goal

The database stops growing with history. Its size is bounded by the hot and warm windows plus one small summary per
stream for everything older, not by the number of events ever written.

Every event keeps its place in its stream, whatever happens to its content: while it is hot or warm its own envelope row
holds that place; once it is cold, a segment summary holds it for the whole cold range. Space is saved by changing where
a body lives or how it is stored, and then by moving whole old ranges out of the database, never by leaving a hole. One
mechanism, the body locator, serves superseding, archiving, erasure and sharing, and one read path resolves it, so
projections, replay, rebuild and integrity never need to know where an event is stored.

## Decisions (owner, 2026-11-19)

1. **Pointers become a primitive of the event store**, not a feature of superseding alone.
2. **Superseding offers every layout below as an option.** A developer may choose any of them per type.
3. **Default:** archive the superseded body (E) when an archive store is configured; otherwise layered (C).
4. **The developer overrides per type.** The evidence deployment's embedding types will be set to envelope-only (D).
5. **Breaking change accepted.** Superseding is used by one consumer today, so its stored layout and defaults may
   change now.
6. **Order of work:** Inline and Superseded first, then Layered, then Archived and Shredded (the warm tier), then the
   cold tier, then Shared.
7. **Archiving is tiered so the database does not keep growing** (owner, 2026-11-19, on review of this plan). Warm: the
   envelope row stays and points at the archived body. Cold: past a second threshold the rows themselves leave the
   database and only the archive holds the events. See Tiers.
8. **Everything here stays in step with the integrity engine** (owner, 2026-11-19). Cleanup, recovery and archiving
   follow the shared, versioned rules of the cleanup and integrity architecture, and nothing cleaned or archived on
   purpose is ever claimed as missing and restored. See Alignment with the integrity engine.

## The model

### Envelope and locator

`wh_event_store` keeps the envelope of every event, always: id, stream, version, type, timestamps, scope, flags,
commit and origin positions, and the digest slot. `wh_event_body` (or its successor) holds a **locator** that says where
the content is and how to rebuild it.

| Kind | Body is | Read returns |
| --- | --- | --- |
| **Inline** | stored in the row, as today | the body |
| **Layered** | a diff plus the id of a base event | base with the diff applied |
| **Archived** | an object in the archive store | the body, fetched from the archive when needed |
| **Superseded** | absent, with the id of the event that replaced it | a marker naming the newer event |
| **Shredded** | absent, key destroyed | a marker saying the content was erased |
| **Shared** | a content hash; one stored copy | the shared body |

Every existing row starts as Inline, so introducing the locator rewrites nothing.

### Tiers

| Tier | Envelope row | Body | What stays in the database |
| --- | --- | --- | --- |
| **Hot** | kept | any locator (Inline, Layered, Superseded, Shared) | everything |
| **Warm** | kept | Archived | the envelope and a pointer, a small fraction of the event (bodies average about 24.6 KB in the evidence deployment) |
| **Cold** | removed | in the archive | one segment summary per stream covering the whole cold range |

- **Warm** is entered after `WarmAfter` (age of the event). It is the Archived locator, so every read, replay and "as
  of" query still finds the event at its position; only the body is fetched.
- **Cold** is entered after `ColdAfter` (a longer age), for a **prefix** of the stream only: the cold range always runs
  from the stream's first event up to a cold floor and never has a gap inside it, so the mid-stream-hole rules are not
  needed. Each cycle extends the floor and merges into the stream's one segment summary, so a stream's cold history
  costs one row however long it is.
- **The segment summary** keeps what the database must still answer without the archive: stream, first and last
  version, event count, first and last commit and origin positions, first and last timestamps, the range's digest
  contribution and seal, and the archive objects that hold it. It is the stream's lifecycle row with its floor and
  carried digest, plus the archive references (Alignment with the integrity engine).
- **Gates for cold** are stricter than for warm. A range goes cold only when every consumer's cursor and every rewind
  floor is above it (a snapshot exists above the floor, so a rebuild never needs to read below it), no hold is active,
  and nothing hot or warm points into it (a Layered base is re-based first; a Superseded marker and its target go cold
  together or not at all). These are the reference rules of Deletion and references, applied to rows.
- **Cold needs an archive store.** Without one there is no cold tier; removing rows with nowhere to keep them is
  destruction, which stays under the existing retention and destruction rules.

### Superseding layouts

What happens to the older copy when a newer one arrives, per type:

| Option | Older copy | Newer copy | History at the older position | Space saved |
| --- | --- | --- | --- | --- |
| **E. Archive** (default with an archive store) | Archived | Inline | the original value, read through | the older body leaves the hot store |
| **C. Layered** (default without one) | Inline (becomes the base) | Layered: diff against the older copy | the original value | the newer body's unchanged part |
| **D. Envelope only** | Superseded, pointing at the newer copy | Inline | a marker | the older body |
| **B. Replace in place** | its body is replaced by the newer body | removed | the newer value, early | the older body and the newer row |
| **A. Append and reap** (today) | removed | Inline | nothing (a hole) | the older body and row |

B and A rewrite history and stay available only as explicit opt-ins, named for what they do
(`ReplaceInPlace`, `RemoveOlder`). Neither is ever a default.

### Policy

- Declared on the type: `[Superseding(Layout = SupersedeLayout.EnvelopeOnly)]`, or in configuration under
  `Whizbang:EventStore:Superseding:Types:<TypeName>:Layout` (configuration wins, so an operator can change it without a
  build).
- Default `SupersedeLayout.Default`, resolved at startup: `Archive` when an `IArchiveStore` is registered, `Layered`
  otherwise. The startup report names the resolved layout for every superseding type, and says why.
- A layout that needs something missing (Archive with no archive store) fails startup naming the setting, as every
  other explicitly configured feature does.

## Read path

One resolver sits under every read: event store reads, the replay reader, rewind, rebuild, the collective applier,
integrity answers and redelivery.

- **Inline:** no change, no cost.
- **Layered:** read the base and every diff in the chain in one statement (ordered by version), apply them in memory.
  The chain length is bounded (see Layered).
- **Archived:** fetch only when a body is actually needed, never on a rebuild path that skips bodies; one fetch per
  stream object, not per event (the #1975 decisions). The latency is documented.
- **Superseded:** the marker replays as an ordinary event carrying the body it points at, so a replay applies the
  newer value at the older position and again at its own (a superseding type is latest-wins, so applying it twice
  gives the same model). The replay context says it is a marker and names the event it stands in for; a perspective
  that cares can read that, and every other perspective needs no change. Opt-out per type: skip markers.
- **Shredded:** a marker; a perspective that needs the content gets an erasure error naming the event.
- **Cold:** a read, replay or "as of" query below the cold floor fetches the segment's archive objects, one per stream
  range, and serves the events in order with their original versions and positions. A rebuild starts from a snapshot
  above the floor and never needs it. The latency is documented.

Other services always receive a full event over the transport. The locator is storage only, so each service stays
self-reliant.

## Alignment with the integrity engine

This plan exists inside the cleanup and integrity architecture ([cleanup-integrity-architecture.md](cleanup-integrity-architecture.md),
approved 2026-11-15) and changes none of its rules. The requirement: nothing this plan stores differently, archives,
moves to cold or destroys may ever look like a missing event to integrity, so integrity never reports it as a deficit and
never redelivers or restores it. Each mechanism takes one of that plan's three shapes (its P4):

| Mechanism | Shape | How integrity sees it | In the rule fingerprint |
| --- | --- | --- | --- |
| Locator change: Layered, Archived (warm), Superseded marker, Shared | not cleanup | the envelope is present, so the event is present; the digest is unchanged | no: storage is local, and each service may choose differently |
| Superseding, every layout (A to E) | rule-shaped | the existing eligibility (key slot, #2364): the older copy is not eligible whether its row is kept, replaced or removed, so all five layouts fold to the same digest | the superseding rule already is; the layout is not |
| Cold tier, and the archive's own retention destroying a cold range | cut-shaped | the cold floor is the stream's lifecycle floor and the range's digest is the carried digest in the lifecycle row (that plan's 2.3); no comparison, repair or redelivery below the floor | no: a cut is local to the service that made it |
| Shredded | origin-decided | propagated by the origin, as designed | as designed |

Consequences:

- **Digest follows eligibility, never storage or timing.** A locator change, a move to warm or cold, and a physical delete
  never change a digest, lower a seal or trigger re-verification.
- **Consumer sovereignty holds.** A consumer may choose a different layout, a different warm or cold threshold, or no
  archive at all, and the pair still compares equal, because none of those choices is in the fingerprint.
- **The segment summary is the lifecycle row.** The cold tier reuses the lifecycle row's floor and carried digest
  (`carried_lo/hi/count/ilo/ihi`) and adds only the archive object references. No second summary table; the row is
  classified State in the table registry.
- **A destroyed range keeps its tombstone.** When the archive's retention destroys a cold range, the lifecycle row and its
  carried digest stay, exactly as a purge keeps a tombstone today, so integrity can never ask for it back.
- **Recovery respects the locator.** Redelivery and repair treat an envelope with any locator as present. They never
  write below a cold floor, never turn a Superseded, Layered or Archived row back into Inline, and reading through to the
  archive is a read, never a repair.
- **Rule changes go through the pair guard.** A slice that changes the eligibility predicate (the SQL constant or the
  rule semantics) bumps the rule-semantics version, so the fingerprint changes and the pair guard halts mixed-version
  pairs until both sides match. A slice that changes storage only must leave the rule-semantics golden file untouched,
  and the existing build guard fails it if it does not.
- **Proof is the #2342 matrix.** Each slice adds its rows to the cross-mechanism matrix before its code (RED first):
  every locator, layout and tier on the origin side crossed with every one on the consumer side, asserting equal
  digests, no deficit and no redelivery request; a positive control in which a genuinely missing event next to a warm,
  superseded or cold range is still found and repaired; and a mixed-rule-version pair that halts instead of repairing.
  The matrix's digest-follows-eligibility invariant stays the acceptance test.

## Deletion and references

A base, a shared body or an archived object can be removed only when nothing points at it. Every cleanup path (prefix
truncate, whole-stream purge, archive, erasure, the reaper) counts references through one helper, the way the reaper's
gates work today. Erasure follows pointers: shredding a base shreds its layers. Keying encryption per stream rather than
per event makes that natural.

## Slices

Each slice is one red/green increment and one pull request unless noted. Every slice also carries its integrity
alignment (Alignment with the integrity engine): its #2342 matrix rows RED first, and either an unchanged rule-semantics
golden file or a version bump through the pair guard.

### Prerequisites (bug fixes, before slice 1)

- **P1. A cursor that names an event its perspective has passed** (#2432). 104 cursors in the evidence deployment
  name the older copy although the perspective applied the newer one and its row is at the newer version; 95 date from
  one day. Find the write path that applies an event without moving the cursor; RED test first.
- **P2. The reaper's cursor gate** (#2433). When the newer copy of the key is already applied, the cursor moves on to it (as a
  slot is handed on), so a lagging pointer cannot hold space forever.
- **P3. Tables larger than the online-rewrite budget are never rewritten** (#2434). `OnlineRewriteMaxBytesPerCycle` (16 GB in
  the evidence deployment) excludes the event store and event body tables, which hold most of the index bloat (about
  4 GB in the busiest service). A table over the budget gets a cycle of its own.

### S1. The locator, with Inline and Superseded (option D) (#2435)

- **Schema.** A locator kind and target on the body row (new migration; existing rows read as Inline). A free
  `EventFlags` bit marks a row whose body is not Inline, so the common read pays nothing.
- **Superseding.** `SupersedeLayout` on `[Superseding]` and in configuration; `EnvelopeOnly` implemented. The reaper
  stops deleting rows: it turns the older copy's body into a Superseded locator and keeps the envelope.
- **Read path.** The resolver, with Inline and Superseded; a marker replays as the event it points at, with the
  marker context (Read path).
- **Existing data.** Copies already queued for superseding are converted to the configured layout, not deleted: once
  P1 and P2 release them, the reaper converts them on its normal cycle (104 in the evidence deployment). Older copies
  removed before this change are gone and stay gone.
- **Breaking change.** `RemoveOlder` (today's behavior) remains as an explicit option; the default changes. CHANGELOG
  **Upgrade:** note.
- **RED tests.** A superseded event keeps its version, timestamp and causation; a replay gives the same model as the
  live run; a marker replays the newer body with the marker context set; an "as of" read between the two events
  returns the marker, not a hole; digests, seals and generations are unchanged by the locator change; a queued copy
  from before the change is converted, not deleted.
- **Docs.** `fundamentals/events/superseding-events` rewritten around layouts; a new `fundamentals/events/body-locator`
  page.

### S2. Layered (option C) (#2436)

- **Diff format.** JSON Merge Patch (RFC 7386) over the stored JSON body: simple, deterministic and readable in SQL.
- **Adaptive.** A newer copy is stored layered only when its diff is under a threshold of its full body
  (`Whizbang:EventStore:Superseding:LayeredMaxDiffRatio`, default 0.5); otherwise it is stored Inline and becomes the
  next base. A wholesale change therefore never pays a read cost for no saving.
- **Chain bound.** At most `LayeredMaxChain` diffs (default 8) on one base; the next write re-bases.
- **Wholesale warning.** The startup report warns when a superseding type's measured diff ratio stays above the
  threshold (it saves nothing under Layered) and suggests EnvelopeOnly or Archive. On by default; turned off per type
  or globally by configuration.
- **Measure first.** Before building, measure the diff ratio of the permissions snapshot type on the evidence
  deployment, to confirm the saving.
- **RED tests.** Reading a layered event returns exactly the original body; a chain at its bound re-bases; a base
  cannot be deleted while a layer points at it; a type whose diffs stay above the threshold raises the warning, and
  the setting silences it.

### S3. Archived and Shredded (#1975)

- **Archived** is #1975, brought into 1.0: event-level archive in both stores, body fetched in place, one object per
  stream per cycle with a minimum batch, archive retention under its own rules. It is the default superseding layout
  when an archive store is configured, and the ephemeral reaper's `Disposition.Archive` stops being refused.
- **Shredded** wires `Disposition.CryptoShred`: per-stream keys, the body destroyed, the envelope and position kept.
- **RED tests.** A read of an archived body equals the original; a shredded event keeps its position and returns the
  erasure marker; erasing a base erases its layers.

### S4. The cold tier (#2437)

- **Warm and cold thresholds.** `WarmAfter` moves bodies to the archive (S3's Archived locator); `ColdAfter` moves a
  stream's prefix out of the database into one segment summary. Both settable globally and per stream type.
- **Segment summary.** The stream's lifecycle row: the cold floor is its floor and the cold range's digest is its
  carried digest, extended each cycle; it adds versions, positions, timestamps and archive object references.
- **Gates.** Every cursor and rewind floor above the range, no hold, nothing hot or warm pointing into it; pointers are
  re-based or moved with the range first.
- **Read path.** Below the cold floor, reads fetch the segment's objects and serve the events in order.
- **WS-3 folds in.** The existing whole-stream and prefix archive becomes the cold tier (a whole stream that ends is a
  cold range covering all of it), and the read-through decorator is replaced by the resolver.
- **RED tests.** A cold range answers integrity without the archive; a read below the floor returns the original events
  at their original versions; a range with a cursor, a rewind floor, a hold or an inbound pointer does not go cold; the
  stream's cold history stays one row however many cycles run; the database size stops growing under a steady write
  load once the warm and cold windows are reached.

### S5. Shared (#2438)

- Content-hash bodies stored once, with a reference count. Smallest measured win (about 0.6% exact duplicates), so it
  comes last and only if a later measurement justifies it.

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Whizbang:EventStore:Superseding:Types:<Type>:Layout` | `Default` (Archive with an archive store, else Layered) | the layout for one superseding type |
| `Whizbang:EventStore:Superseding:LayeredMaxDiffRatio` | 0.5 | above it, the newer copy is stored whole |
| `Whizbang:EventStore:Superseding:LayeredMaxChain` | 8 | diffs on one base before a re-base |
| `Whizbang:EventStore:Superseding:Types:<Type>:MarkerReplay` | `Deliver` | `Deliver` replays a marker as the event it points at, with the marker context; `Skip` passes over it |
| `Whizbang:EventStore:Superseding:WholesaleWarning` | `true` | warn at startup when a type's diffs stay above `LayeredMaxDiffRatio`; also settable per type |
| `Whizbang:EventStore:Tiers:WarmAfter` | 30 days when an archive store is configured | age after which a body moves to the archive; needs an archive store |
| `Whizbang:EventStore:Tiers:ColdAfter` | 365 days when an archive store is configured | age after which a stream's prefix leaves the database; needs an archive store; must exceed `WarmAfter` |

Every key is bound, has a default, and appears on the configuration reference and the error reference pages.

## Decided questions (owner, 2026-11-19)

1. **#1975 scope.** Archive is the default when an archive store is configured, so #1975 comes into 1.0 (S3).
2. **Wholesale types under Layered.** The startup report warns, and the warning can be turned off (S2).
3. **Marker replay.** A marker replays as the event it points at, even though that value then appears twice in the
   stream, and carries context saying it is a marker (Read path).
4. **Existing data.** Convert the copies already queued (S1).
5. **Stream-level archive.** Superseded by decision 7: the stream-level archive becomes the cold tier (S4).

## Decided after review (owner, 2026-11-19)

1. **Default thresholds.** With an archive store configured, warm and cold are on by default: `WarmAfter` 30 days,
   `ColdAfter` 365 days. The evidence deployment will set `ColdAfter` to 90 days in its own configuration (an adoption
   step there, not a framework default).
2. **After the archive's own retention.** The lifecycle row and its carried digest stay as a tombstone, as the approved
   cleanup and integrity plan already requires for a purge (its 2.3), so integrity never asks for a destroyed range back.
