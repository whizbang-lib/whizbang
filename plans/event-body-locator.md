# Event body locator: pointers in the event store

Part of [storage-reclamation.md](storage-reclamation.md). Builds on [storage-event-slimming.md](storage-event-slimming.md)
(superseding types, WS-2) and [storage-stream-end-of-life.md](storage-stream-end-of-life.md) (archive store and
read-through, WS-3). Absorbs #1975 (event-level archive). Release: 1.0. Tracking: #2431. Status: approved plan, owner questions decided (2026-11-19).

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

Every event keeps its place in its stream forever, whatever happens to its content. Space is saved by changing where a
body lives or how it is stored, never by removing the event. One mechanism, the body locator, serves superseding,
archiving, erasure and sharing, and one read path resolves it, so projections, replay, rebuild and integrity never need
to know how a body is stored.

## Decisions (owner, 2026-11-19)

1. **Pointers become a primitive of the event store**, not a feature of superseding alone.
2. **Superseding offers every layout below as an option.** A developer may choose any of them per type.
3. **Default:** archive the superseded body (E) when an archive store is configured; otherwise layered (C).
4. **The developer overrides per type.** The evidence deployment's embedding types will be set to envelope-only (D).
5. **Breaking change accepted.** Superseding is used by one consumer today, so its stored layout and defaults may
   change now.
6. **Order of work:** Inline and Superseded first, then Layered, then Archived and Shredded, then stream-level archive as locators (added with question 5), then Shared.

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

Other services always receive a full event over the transport. The locator is storage only, so each service stays
self-reliant.

## Integrity

The digest covers the logical event, not its storage. A locator change (Inline to Archived, to Layered, to Superseded,
to Shared) never changes a digest, a seal or an origin generation: physical storage is timing, and the digest follows
eligibility only (cleanup and integrity decision 2, 2026-11-15). Superseding keeps counting the key's slot (#2364).
Shredding is the one origin-decided change and propagates as it is designed to.

## Deletion and references

A base, a shared body or an archived object can be removed only when nothing points at it. Every cleanup path (prefix
truncate, whole-stream purge, archive, erasure, the reaper) counts references through one helper, the way the reaper's
gates work today. Erasure follows pointers: shredding a base shreds its layers. Keying encryption per stream rather than
per event makes that natural.

## Slices

Each slice is one red/green increment and one pull request unless noted.

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

### S4. Stream-level archive as locators (#2437)

- The whole-stream and prefix archive (WS-3) writes Archived locators and keeps the envelope rows, instead of deleting
  them and filling the gap through the read-through decorator. Judged necessary: it leaves one read path and one
  reference rule for every archived body, removes the mid-stream-hole rules, and keeps every position, version and
  digest answerable without the archive. The decorator retires once nothing reads through it.
- **RED tests.** An archived prefix keeps its envelopes and versions; a read of an archived body equals the original;
  integrity answers for an archived range without fetching it.

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

Every key is bound, has a default, and appears on the configuration reference and the error reference pages.

## Decided questions (owner, 2026-11-19)

1. **#1975 scope.** Archive is the default when an archive store is configured, so #1975 comes into 1.0 (S3).
2. **Wholesale types under Layered.** The startup report warns, and the warning can be turned off (S2).
3. **Marker replay.** A marker replays as the event it points at, even though that value then appears twice in the
   stream, and carries context saying it is a marker (Read path).
4. **Existing data.** Convert the copies already queued (S1).
5. **Stream-level archive.** Yes where necessary; judged necessary and planned as S4, ahead of Shared.
