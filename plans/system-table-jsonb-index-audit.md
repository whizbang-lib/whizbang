# What Whizbang's own tables are filtered by, and what they are indexed for

## Scope

Started as a jsonb-index question and widened, correctly, to every system table: what does the
framework actually filter these tables by, and is there an index for it. The jsonb finding is below;
the larger finding turned out to be on ordinary columns.

## Why this exists

The perspective work established that a GIN index with the default operator class answers containment
and existence and nothing else, and that an index nothing reaches is pure cost: maintained on every
write, scanned never. Applying that to the framework's own tables turned up the inverse problem. Nine
system tables hold jsonb. **Not one of them carries a single index over it.** No GIN, no expression
index, anywhere.

That is not automatically wrong. An index over a column nothing filters is the waste we just removed
elsewhere. So the question this audit answers is not "which jsonb columns lack an index" but "which
jsonb columns are **filtered by a real query** and lack an index", which is a much shorter list.

## The inventory

| Table | jsonb columns | Index over them | Filtered by a query? |
|---|---|---|---|
| `wh_event_store` | `event_data`, `metadata`, `scope` | none | **yes, `scope`** |
| `wh_event_body` | `event_data`, `metadata` | none | refinement only |
| `wh_event_archive` | `event_data`, `metadata`, `scope` | none | no |
| `wh_inbox` | `event_data`, `metadata`, `scope` | none | no |
| `wh_outbox` | `event_data`, `metadata`, `scope` | none | no |
| `wh_dead_letters` | `envelope`, `metadata` | none | no |
| `wh_log` | `metadata` | none | no |
| `wh_request_response` | `request_data`, `response_data` | none | no |
| `wh_service_instances` | `metadata` | none | no |

**The hot path is healthy and is not the problem.** The inbox and outbox are indexed heavily and well,
on real columns: status with lease expiry, partition with instance, stream with arrival, coalesce
group, scheduled-for, attempts, failure reason. Their claim and drain queries never touch their jsonb
at all, so their jsonb columns are correctly unindexed and adding anything there would be write
amplification on the hottest tables in the system for no read.

The same holds for dead letters, the log, request/response and service instances: they write jsonb and
never filter it.


## The sweep: every predicate applied to a system table, against every index on it

Collected by extracting each aliased `<table>.<column> <operator>` predicate across all migrations and
all driver SQL, then diffing the filtered columns against the indexed leading columns. Counts are
occurrences in source, which measure how many code paths touch a column rather than how hot any one
of them is.

### wh_event_store: the outlier

| Filtered by | Occurrences | Index that serves it |
|---|---|---|
| `event_id` | 91 | primary key |
| `stream_id` | 67 | `(stream_id, version)` |
| **`event_type`** | **41** | **none** |
| **`flags`** | **38** | **none** |
| `commit_sequence` | 32 | `(commit_sequence)` partial |
| `version` | 26 | leading column of two |
| **`origin_service_id`** | **19** | **none** |
| **`created_at`** | **13** | only behind `aggregate_type` |
| **`origin_commit_sequence`** | **11** | **none** |
| **`scope->>'t'`, `'u'`, `'o'`, `'c'`** | lens surface | **none** |

`event_type` appears in two shapes and both hit an unindexed 500-character column on the largest table
in the system. As a **filter**: `es.event_type = ANY(v_types)` in the query and replay paths, and an
`EventTypeFilter` array pulled out of an inquiry document. As a **join key**: against the association
registry (`ma.normalized_message_type`) and the ephemeral grace table. A join on an unindexed column of
the big table leaves the planner hashing the whole store or nested-looping with a scan.

### The hot path is healthy and is not the problem

The inbox and outbox are indexed thoroughly and thoughtfully: eighteen and twenty-two indexes
respectively, most of them partial, several covering. Claim, drain, scheduling, coalescing, priority
banding, lease expiry and orphan handling all have an index shaped for them. Every column those tables
are filtered by is served, and their jsonb is not filtered at all.

`wh_inbox.message_type` appears twice, in the orphan purge, as `<> ALL(...)`. An anti-match is not
index-servable in general, and that statement is driven by `processed_at IS NULL`, which is indexed. So
it is correctly unindexed.

Dead letters, the log, request/response, service instances and perspective events are likewise covered
for what they are filtered by.

### Candidates, and what has to happen before any of them is added

The audit produces candidates, not conclusions. Every index in the perspective work earned its place by
being shown to be *chosen* by the planner on a table large enough that a sequential scan would
otherwise win, and the same bar applies here. Adding an index because a predicate exists is how the 261
unused ones got there.

| Candidate | For | Still to prove |
|---|---|---|
| `(event_type)` | the type filter and the two joins | that it is chosen rather than the scan, at realistic cardinality |
| `((scope->>'t'))` | the filterable event-store lens | the same, and whether the other three keys are used enough to earn their own |
| `(created_at)` | the reaper's bare range | whether the existing composite is genuinely unusable here |
| `(origin_service_id)` | origin filtering | how selective it is; it may be low-cardinality |

`flags` is deliberately absent. It is a bitmask tested with `&`, which a plain btree cannot serve, and
its selectivity is low; a partial index on a specific flag combination is the shape that would work, and
only if a real query is shown to want it.

## Finding 1: the event store's scope filter has no index at all

`EFCoreFilterableEventStoreQuery` is a documented, user-facing lens over the event store. It filters
on the scope document by tenant, user, organization and customer:

```
WHERE scope->>'t' = ?    -- tenant
  AND scope->>'u' = ?    -- user
  AND scope->>'o' = ?    -- organization
  AND scope->>'c' = ?    -- customer
```

There is no index on any of them, so every such query reads the whole event store. This is the exact
shape a perspective already solves: every perspective table carries
`CREATE INDEX ... ON tbl ((scope->>'t'))` for precisely this filter, added because the tenant
predicate of every lens query and every collective apply lands on it and the planner otherwise
sequentially scans.

The event store is the worst table in the system to scan. It is the largest, it only grows, and
unlike a perspective it is never rebuilt.

The fix is the one already proven next door: a btree expression index over the extraction. The cast is
text-to-text, which is immutable, so there is nothing blocking it.

**Discipline:** the tenant key is the one to index first, because it is the one every scoped query
carries. The other three are optional refinements a caller adds, so indexing all four speculatively is
the mistake this audit exists to avoid. Measure which are actually used before adding more.

## Finding 2: a bare `created_at` range on the event store has no index

The reaper filters `es.created_at < NOW() - grace`. The only index containing `created_at` is
`(aggregate_type, created_at)`, whose leading column is not in the predicate, so it cannot serve a bare
range. The reap therefore scans.

This is a real column rather than jsonb, but it turned up in the same audit and has the same shape:
a predicate a maintenance cycle runs regularly with nothing to answer it.

## Finding 3: the event store's dates cannot be indexed, and that is a consequence of a decision

`wh_event_body.metadata ->> 'ephemeral_expires_at'` is cast to `timestamptz` by the reaper, and
`metadata ->> 'deliveryGuarantee'` to `integer` by the digest. The timestamp cast is `STABLE`, so
PostgreSQL will not build an index over it, which is the same wall the perspective dates hit.

Perspectives solved it by storing the canonical numeric form. **The event store deliberately did
not.** Its documents are serialized under the default profile, which is the transport profile: a date
in an event payload is read by other systems and by older releases of this one, so changing it is a
wire-format break rather than an internal decision about a table this library owns. That trade was
taken when the canonical form was scoped to the persistence profile, and a test pins it.

So an indexable event-store date needs one of:

- **A real column.** The value is promoted out of the document, typed, and indexed. This is the
  available answer today and the one the framework already uses for everything on the hot path.
- **A separate event-store profile.** The transport profile keeps its renderings while the stored form
  becomes canonical. This is a genuine option, since what goes on the wire and what lands in the table
  need not be the same bytes, but it is a real migration over the largest table in the system.

Neither is warranted by the two predicates found, because both are **refinements** rather than driving
predicates: the reaper is driven by `created_at` and the digest by a `commit_sequence` range, and both
join the body table by its primary key. An index on the refinement would be built and never chosen.
Recorded here so the next person does not re-derive it, and so that if a date predicate ever becomes a
driving one, the two options are already written down.

## What this audit deliberately does not do

Add a GIN index to every jsonb column. That is how the 261 unused indexes and the 984 MB they cost got
there. An index earns its place by being reached by a query, demonstrated on a table large enough that
a sequential scan would otherwise be the cheaper plan, which is how every index in the perspective work
was justified.

## A finding from the same sweep, on the write path rather than the read path

`BaseUpsertStrategy` resolves its persistence serializer options **per upsert**, uncached: every row
written rebuilds a `JsonSerializerOptions`, recombines every registered resolver, and re-adds every
converter. That was deliberate, and the comment says why: the previous shape was a process-wide single
slot that only ever held one assembly's view and raced across tests. Correctness first, and correctly
so.

But the cost is real and it is on the hottest write path in the system. A fresh options instance also
means the serializer's own per-options type metadata cache is cold on every call, which is the larger
half: the resolver combination is cheap next to re-resolving type metadata for the whole model graph
each time a row is written.

Not changed here, and deliberately not. Undoing a caching decision that was made to fix a real race,
in the same change that alters the stored format, would make a regression in either impossible to
attribute. It wants its own change, its own measurement, and a cache keyed so that a late assembly
registration still invalidates it.

Recorded because it was found while auditing what these tables cost, and because the next person to
profile an import will find it and deserve to know it was seen and left on purpose.
