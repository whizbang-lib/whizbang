# Perspective stored forms: one unit, two paths, one ledger

**Read when**: touching how a perspective document stores or reads a date, time, duration or
identifier; touching the persistence serialization profile, the EF convention, the JSON reader/writers,
the stored-form rewrite, or the perspective worker's failure path; or reading a
"cannot read its stored document" error.

This is the distilled record of a split-brain that stopped a feature in a deployed service and of the
design that closed it. The full design and its progress table are in
`plans/archive/canonical-temporal-storage-unification.md`; the user-facing pages are
`fundamentals/perspectives/jsonb-containment` and `operations/infrastructure/migrations` on the docs
site.

## The one rule

Every date, time and duration in a perspective document is stored as **one number in one unit,
microseconds**: an instant since the Unix epoch, a date the same at its midnight UTC, a time of day
since midnight, a duration plain. `CanonicalTemporalFormat` is the only place the arithmetic lives.

A day count and a microsecond count are both integers and nothing in a document says which unit a
number is in. Two units in one family is a number a reader can only guess at. Never add a second
unit; never let a reader reinterpret a number.

## Two storage shapes, two readers, one reader

A model is stored **mapped** (`ComplexProperty().ToJson()`, EF reads it with its own JSON reader
property by property) or **opaque** (`Property().HasColumnType("jsonb")`, System.Text.Json reads the
whole document). Records, polymorphic members and members the mapped path cannot materialize are
opaque (`MappedPathDiscovery.MustStoreOpaquely`).

Both paths must read and write the same bytes. The pieces, and why each exists:

| Piece | Path | What it does |
|---|---|---|
| `CanonicalTemporalJsonConverters`, registered on `SerializationProfile.Persistence` at priority 100 | opaque write and read | Applies wherever the type occurs: inherited, nested, collection element, positional record parameter, framework metadata |
| `CanonicalTemporalConvention` (`IModelFinalizingConvention` via `IConventionSetPlugin`, and `Apply(ModelBuilder)` called by the generated OnModelCreating) | mapped | Walks the model EF built and sets a `ValueConverter<T,long>` and a JSON reader/writer on every temporal inside a document. The plugin rides the Whizbang options extension; the generated OnModelCreating applies the same walk itself so a context built from a plain connection string (a hand-built lens context) converts too |
| `CanonicalTemporalJsonReaderWriters` | mapped read and write | EF's own integer reader refused a rendering and refused an unexpected token with a generic error; these put the same reader on the mapped path |
| `CanonicalTemporalReaders` | both | One reader per kind: number in the canonical unit, or a rendering (counted), or a `JsonException` naming the type, the forms accepted and the token found |
| `PerspectiveDocumentSerialization.ConverterFor<T>()` | opaque | Binds the `data`, `metadata` and `scope` columns to the Persistence profile explicitly |

The data source's JSON options **cannot** move to the Persistence profile: the same options serve
the outbox, inbox and event store metadata columns, whose form is the wire's. That is why the opaque
columns are bound explicitly instead.

Nothing per property is generated any more. The generator once emitted `HasConversion` per property
it discovered and a per-model converter modifier; every placement the discovery missed was a place
the writer and the reader disagreed. Do not bring that back.

## The ledger and the rewrite

`wh_perspective_forms` (migration 153) records, per table, the form it is in: absent means the
mixed-unit form (1), 2 is microseconds, `settled_at` set means a pass at form 2 found nothing left.
`wh_canonicalize_temporal` rewrites one path of one document and leaves anything it cannot parse
exactly as it was.

`CanonicalTemporalRewrite.ForModel(IModel, JsonSerializerOptions, schema)` derives the paths at
startup from EF's model (mapped) and from `JsonTypeInfo` metadata (opaque), and emits one DO block
per table that calls the function per path and upserts the ledger row. It runs after the migrator
election, on whichever instance goes on to do the schema work: the migrator, an instance that could
not be staged, or a waiter whose deferral ended with the migrator gone. A table this release creates
is recorded at form 2 and settled before its `CREATE TABLE`.

`CanonicalTemporalRewritePhase.ApplyAsync` runs those statements under the schema-init key, the same
key the bootstrap and DDL phases take, in one transaction of its own with a savepoint per table.
Transaction scope because a transaction pooler cannot then separate the lock from the statements and
nothing stays held if the instance dies; a savepoint per table so one table's failure neither undoes
an earlier table nor stops a later one. **An instance that cannot take the key waits for it** (poll
with backoff, up to the schema command timeout). It never skips: the holder may be a sibling's
bootstrap or DDL transaction, which converts nothing, and a sibling staged as a waiter never rewrites,
so a skip on a lost attempt left every table of a schema unconverted on a fleet that started
together, with nothing above debug level to say so. Running out of the budget is a warning naming the
key; the tables stay in their current form, which every reader tolerates, and the next start tries
again.

What the log says, at Information, on the instance that ran it:

```text
Stored-format rewrite is waiting for schema lock {LockId}, held by another instance
Stored-format rewrite: wh_per_thing: converted, 7 row update(s)
Stored-format rewrite: wh_per_other: settled, skipped
Stored-format rewrite: wh_per_gone: table absent, nothing to convert
Stored-format rewrite applied 3 of 3 table statement(s) under schema lock {LockId} in 1840 ms
```

The per-table lines are notices the DO block raises and the phase relays; the count is one per row
and path, so a row with three converted keys counts three times. A table that failed is a warning
naming it, with the exception.

A unit-changing release is not safe under a mixed fleet. `FleetVersions.OtherLiveVersionsAsync` names
the other releases alive in `wh_service_instances` and the initializer logs them at Warning before
rewriting. Deploy without a mixed fleet.

A date's index used to cast through `int4`; it now casts through `int8` like every kind, and because
`CREATE INDEX IF NOT EXISTS` would keep the old index under the old name, `JsonIndexInfo.Superseded`
renames it and drops the old one.

## Objects a consumer owns over a temporal key

Anything a consumer creates over a perspective document's temporal key reads the canonical number:
a trigger, a view, a generated column, an expression index, a mirror table's backfill. A cast of the
key's text to `timestamptz` was written for the rendering and fails on the number, and it fails in
two places: the rewrite's `UPDATE` of that table (reported as a failed table, the rest of the pass
unaffected, the table left unconverted) and then every later write the framework makes into that
table once it stores the number. The framework cannot know such an object exists, so the rewrite's
warning naming the table is the signal.

During a transition the object reads either form:

```sql
CASE jsonb_typeof(data -> 'PublishedAt')
  WHEN 'number' THEN TIMESTAMPTZ 'epoch' + ((data ->> 'PublishedAt')::bigint * INTERVAL '1 microsecond')
  WHEN 'string' THEN NULLIF(data ->> 'PublishedAt', '')::timestamptz
  ELSE NULL
END
```

Interval arithmetic rather than `to_timestamp(n / 1e6)`, which goes through a double and can lose
the last microsecond. A date key is the same number at midnight UTC; a duration is
`n * INTERVAL '1 microsecond'` with no epoch.

## Tolerance, and when it goes

Readers are strict about units and, for now, tolerant of renderings: a rendering is read as the value
it renders and counted on `whizbang.perspective.temporal_form_fallbacks{kind}` (identifiers on
`whizbang.perspective.identifier_form_fallbacks{type}`), announced once per kind at Warning by
`StoredFormFallbacks`. The rendering branch is removed once the counter reads zero across a release
cycle. Removing it earlier makes a row the rewrite did not reach a stopped feature again.

## When a row cannot be read

`StoredFormUnreadable.TryClassify(exception, out failure)` finds a reader's `JsonException` anywhere
in a chain of wrappers or an aggregate and carries the path and the message. The perspective worker,
in both the drain path and the channel path:

1. records the failure in `StoredFormFailureRegistry` and logs event id 65 at Error once per
   (perspective, stream), event id 66 at Debug after that, until the stream reads again;
2. counts `whizbang.perspective.read_failures{perspective_name, reason=stored_form_unreadable}`;
3. reports every leased row of the group through `IFailureChannel` as `WorkCategory.PerspectiveEvent`
   with `MessageFailureReason.SerializationError`, so `process_perspective_event_failures` records the
   failure, schedules the retry with backoff and the dead-letter check reads the counter;
4. the `perspective-stored-forms` health component (`StoredFormHealthSource`) reports Degraded with the
   count while any are remembered.

The inbox lifecycle "(continuing)" swallow logs at Error instead of Warning when the cause is a
deserialization failure (event id 74).

## Three defects found underneath, two fixed here

- **The generated message facade cached metadata by type alone.** `MessageJsonContext`'s thread-local
  `TypeInfoCache` was keyed by `Type`, but a `JsonTypeInfo` is bound to the `JsonSerializerOptions` it
  was created for and a property's converter is chosen from those options. Once the wire profile had
  asked for `DateTime` on a thread, the persistence profile got a date bound to the wire's options and
  the upsert wrote `"2026-...Z"` into a document whose index casts the key to `bigint` (`22P02`), on
  whichever thread the order went that way. The per-model modifiers used to hide it by rewriting the
  cached metadata's converters in place. The cache is now `TypeInfoCacheFor(options)`, a per-thread
  `ConditionalWeakTable<JsonSerializerOptions, Dictionary<Type, JsonTypeInfo>>`
  (`JsonContextSnippets.cs`). Never cache a `JsonTypeInfo` without the options it belongs to.
- **The failure element never matched a row.** `process_perspective_event_failures` read
  `EventWorkId`/`FailureReason` while the runtime serializes `MessageFailure` as `MessageId`/`Reason`.
  No perspective failure sent through the failure channel was ever recorded. Migration 154 reads both
  spellings. The outbox and inbox failure functions read `FailureReason` the same way, so
  `failure_reason` on those rows has always been Unknown; that is a follow-up, not fixed here.
- **A drain-path apply failure parks nothing.** The cursor failure carries `LastEventId = Guid.Empty`,
  which the EF coordinator skips, and the batched strategy then sends that same empty id as the row id.
  Stored-form failures now report per row (above). Any other apply failure still parks nothing; making
  it park changes retry semantics for every consumer's apply failure and is its own decision.

## Tests that pin all of this

Core: `CanonicalTemporalFormatTests`, `CanonicalTemporalReadersTests`,
`CanonicalTemporalReaderToleranceTests`, `CanonicalTemporalJsonConverterTests`,
`PersistenceProfileConverterReachProbeTests`, `StoredFormUnreadableTests`,
`StoredFormFailureRegistryTests`, `StoredFormHealthSourceTests`,
`PerspectiveWorkerDeepPathDrainTests` (StoredForm file), `LifecycleExceptionInvariantTests` (LogLevel
file). EF: `CanonicalTemporalStorageTests`, `CanonicalTemporalConventionTests`,
`OpaqueDocumentRoundTripTests`, `PerspectiveDocumentSerializationTests`,
`CanonicalTemporalRewriteTests`, `CanonicalTemporalRewriteIntegrationTests`,
`CanonicalTemporalRewritePhaseTests` (the wait, the budget, savepoints, notices), `FreshTableFormTests`,
`FleetVersionsTests`, `CanonicalTemporalFunctionTests`, `PerspectiveFailureCounterSqlTests`.
Generators: `CanonicalTemporalConfigurationTests`, `CanonicalTemporalRewriteWiringTests`,
`JsonIndexGenerationTests`.
