# Canonical temporal storage: one unit, one writer rule, one reader rule

> **Status: proposal, awaiting a decision. No code has been written against it.**
> Companion plans: `temporal-backfill-blocks-readiness.md` (startup cost of the rewrite) and
> `lens-full-index-coverage.md` (why the canonical form exists at all).

## 1. What is broken, stated from evidence

The canonical temporal form (a date, time or duration stored as a JSON number so its extraction
reaches an immutable cast and can carry an index) shipped as three independent decisions that happen
to agree for most models and disagree for the rest. Every finding below was verified against the
code and against a live deployment of the release that introduced the form.

### 1.1 Two storage forms, and only one of them has a reader for numbers

A perspective document is stored in one of two shapes, chosen by `MappedPathDiscovery.MustStoreOpaquely`:

| Shape | Mapping | Who reads `data` | Who writes `data` |
|---|---|---|---|
| **Mapped** | `ComplexProperty(e => e.Data).ToJson()` plus generated `HasConversion<long>` per discovered temporal | Entity Framework's own JSON reader, property by property, through the value converter | Path 1 (STJ, Persistence profile, per-model modifier) or EF SaveChanges (converter) |
| **Opaque** | `Property(e => e.Data).HasColumnType("jsonb")` | STJ, through the Npgsql data source's `ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions())`, which is the **Default** profile | Path 1 (STJ, **Persistence** profile, per-model modifier) |

A model is opaque when it is polymorphic, when it is a `record` (the compiler's `EqualityContract`
trips the polymorphism walk), or when it holds a member EF cannot construct, such as a nested
positional record inside a collection.

Three generators each decide independently what to do about an opaque model:

- the persistence context generator emits the canonical **modifier** for it (from `CanonicalTemporalDiscovery.From`, no opaque check);
- the service registration generator emits the startup **rewrite** for it (`_generateCanonicalTemporalRewritesCode`, no opaque check);
- the same generator emits **no index** for it (`_reachableJsonIndexes` checks `MustStoreOpaquely`).

So an opaque document with a temporal is written as a number by Path 1, rewritten to a number at
startup, and read by a Default-profile options object whose only temporal reader is
`LenientDateTimeOffsetConverter`, which throws on a Number token. Every read of every such row
fails, forever. In the deployment where this was found, the failing model was on the critical path
of an interactive feature, and the feature stopped.

Nothing in the design states the invariant that would have prevented this: **a document is read
under the profile it was written in.** The data-source options are the Default profile because
that was the historical default, not because anyone chose it for opaque documents.

### 1.2 Three units

| Kind | Stored as | Index cast |
|---|---|---|
| Instant (`DateTime`), OffsetInstant (`DateTimeOffset`) | microseconds since the epoch | `int8` |
| TimeOfDay (`TimeOnly`) | microseconds since midnight | `int8` |
| Day (`DateOnly`) | **days** since the epoch | **`int4`** |
| Duration (`TimeSpan`) | **ticks** (100 ns) | `int8` |

A day cannot be compared with an instant, a duration cannot be added to one, and ticks are a .NET
unit no SQL reader would guess. The consumer asked for the obvious thing: everything in one unit,
so that any two temporal fields order against one another with the same cast.

### 1.3 Discovery is partial, and the partiality is currently what keeps it safe

`CanonicalTemporalDiscovery._from` reads `model.GetMembers()` only: no base-type walk, no descent
into nested objects, no descent into collection elements. A temporal declared on a base class is
invisible to the modifier, the rewrite, the `HasConversion` and the index alike, so it stays a
string on both sides and works. That is luck, not design. Any change that widens one side (a
global converter, say) without widening every other side turns each of these gaps into a read
failure, because EF's JSON reader for a `HasConversion<long>` property reads a number token and a
string breaks it.

### 1.4 Framework documents are excluded on purpose

`PerspectiveMetadata.Timestamp` is a `DateTime` written under the Persistence profile by Path 1 and
mapped by EF with no conversion, so it is a string. The persistence context generator's comment
gives this as the reason the converter is per model rather than per profile. The consequence is
that the framework's own timestamp cannot be ordered or indexed with the same cast as the model's.

### 1.5 Readers are strict

Each canonical converter accepts exactly one token type. A reader that accepts every form the
framework has ever written costs a token-type branch, and would have made 1.1 a non-event
(a slow query instead of a stopped feature).

### 1.6 The failure is loud but unclassified, and it never stops

The read failure surfaces as an Error per drain cycle ("Error processing perspective X for stream
Y") followed by EF's generic iteration error and a stack trace. Nothing names the property, the
token that was found, or the forms that were expected; there is no metric; and the same stream is
retried every cycle indefinitely. An operator sees a wall of identical errors and has to read a
stack trace to learn it is a storage-form problem.

### 1.7 No test drives one path's write into the other path's read

The byte-format integration tests never covered a temporal. No test writes a model through Path 1
and reads it back through the opaque EF mapping. The generator tests pin each emitter separately.
The three emitters in 1.1 were each tested and each correct in isolation.

## 2. Constraints, found rather than assumed

- **Wire safety.** A perspective model type can double as a message or contract type in a
  consumer. Canonical conversion must stay on the Persistence profile. This is already pinned by
  `AModifierDoesNotReachAnotherProfileAsync` and `TheTransportProfileStillWritesARenderingAsync`;
  both survive this design.
- **The data-source JSON options have exactly one consumer**: opaque perspective columns (`data`,
  `metadata`, `scope`). The event store, outbox, inbox and work coordinator all deserialize through
  explicit options. Changing the data-source profile has a bounded blast radius.
- **Path 1 already writes every model, opaque included, under Persistence.** That profile stores
  `[WhizbangId]` values in object mode, and the Default profile's scalar reader (`reader.GetString()`)
  cannot read that. Any opaque model holding a `[WhizbangId]` member is therefore broken today in
  exactly the way 1.1 is; it has simply not been hit yet. The same change fixes both.
- **EF-side reads are not tolerant.** For mapped models the rewrite must complete before the
  service serves, as it does today. Reader tolerance (section 3.3) is insurance for opaque
  documents and for rows the rewrite has not reached; it does not replace the rewrite.
- **A number is a number.** Converting days or ticks to microseconds cannot be made idempotent by
  inspecting values (a day count and a microsecond count are both integers). The unit rewrite needs
  a ledger.
- **`CREATE INDEX IF NOT EXISTS <name>`** never rebuilds an index whose expression changed under the
  same name. The Day cast change must rename.
- **Data in the field is in five shapes**: strings the discovery never found, microsecond numbers,
  day numbers, tick numbers, and string metadata timestamps. All five must be handled, and the
  handling must be idempotent and interruptible.
- **Older releases cannot read canonical rows.** Already true; the design does not make it worse
  and does not try to make it better. The migration is one-way.
- The generated context uses **no compiled model and no EF conventions** today.

## 3. Design

### 3.1 One unit: microseconds, for every kind

| Kind | CLR | Canonical value | Origin |
|---|---|---|---|
| Instant | `DateTime` | microseconds since the Unix epoch, UTC | epoch |
| OffsetInstant | `DateTimeOffset` | microseconds since the Unix epoch, UTC | epoch |
| Day | `DateOnly` | microseconds since the Unix epoch at **midnight UTC** of that day | epoch |
| TimeOfDay | `TimeOnly` | microseconds since midnight | midnight |
| Duration | `TimeSpan` | microseconds | zero |

Every kind is `int8`, every extraction is `((data ->> 'X')::bigint)`, every EF conversion is
`HasConversion<long>`. A day orders against an instant; an instant plus a duration is an instant;
a time-of-day is a duration from midnight. `infinity` and `-infinity` keep the existing sentinels
(`MAX_MICROSECONDS`, `MIN_MICROSECONDS`); Day gets the same two sentinels for `DateOnly.MaxValue`
and `MinValue`.

**From what is stored today to this.** The conversion is exact for every kind, because the
mixed-unit release wrote each kind deterministically, so there is no guessing involved:

| Stored today | Conversion | Exact? |
|---|---|---|
| Instant, OffsetInstant, TimeOfDay as microseconds | none | already the target form |
| Day as days | multiply by 86,400,000,000 | yes |
| Duration as ticks | divide by 10 | truncates the sub-microsecond digit; PostgreSQL `interval` has no finer resolution either |
| Any kind as a rendering (a path the discovery never found) | parse to microseconds, as the existing rewrite does | yes |
| `metadata.Timestamp` as a rendering | parse to microseconds | yes |

Within one table a given path is in one form, never a mix: a discovered path was rewritten to
numbers before the release served and has been written as numbers since; an undiscovered path is
renderings throughout. So for any (table, path): a string parses, a number converts from the unit
the ledger says (3.6), and no value is ever inspected to decide which.

An environment that never ran the mixed-unit release (still on renderings) converts straight to
microseconds and never sees a day count or a tick count. An empty database creates every table at
form 2 and converts nothing.

Accepted precision changes, to be stated in the docs: `TimeSpan` loses its sub-microsecond digit
(100 ns resolution to 1 us), which is the resolution PostgreSQL's `interval` has anyway; `DateTime`
already loses it. `DateTimeOffset` continues to store the UTC instant and reads back with a zero
offset; the offset is not preserved. Unchanged from today, but it belongs in the same table.

### 3.2 One writer rule: the Persistence profile converts every temporal

Register the five canonical converters as options converters on the Persistence profile
(`JsonContextRegistry.RegisterConverter(converter, priority, SerializationProfile.Persistence)`)
from the Core module initializer, the same mechanism that puts `LenientDateTimeOffsetConverter` on
the Default profile. STJ applies an options converter to every occurrence of the type in every
document: nested objects, collection elements, inherited members, `PerspectiveMetadata`,
`PerspectiveScope`, and anything a consumer adds later. There is nothing to discover.

Remove the per-model `RegisterTypeInfoModifier` emission from `PerspectivePersistenceJsonContextGenerator`
and the `CanonicalTemporalJsonConverters.ApplyTo` API it targets. The Default profile is untouched,
so a model that crosses the wire still renders as a string there.

### 3.3 Reader tolerance: transitional, measured, and then removed

The conversion in 3.6 is SQL and does not need a tolerant C# reader. Tolerance exists for one
reason only: a rendering the rewrite did not reach (a path the extended discovery still misses in an
opaque document, or a row an older instance wrote during the rollout window) would otherwise stop a
feature until the next release, which is the failure this design comes from. That is also the
argument against it: a reader that absorbs an unconverted form hides the gap in the rewrite.

So tolerance is scoped, observable, and temporary:

- **Strict about units, always.** A `Number` is read in the canonical unit (3.1), full stop. No
  reader ever guesses whether a number is a day count, a tick count or a microsecond count; that
  question is answered by the ledger in 3.6 before any reader sees the row.
- **Tolerant of renderings, for now.** A `String` parses as the rendering (ISO 8601, and the
  `infinity` / `-infinity` and missing-offset cases `LenientDateTimeOffsetConverter` already handles).
  Anything else is a `JsonException` naming the CLR type, the token found and the forms accepted;
  STJ appends the JSON path.
- **Every fallback is counted.** Reading a rendering increments
  `whizbang.perspective.temporal_form_fallbacks` (tags: perspective, path, kind) and logs once per
  (perspective, path) at Warning: "read a rendering where a canonical number was expected; the
  rewrite did not reach this path". A non-zero meter is a rewrite bug with a path attached.
- **Removed in a later release.** Once the meter reads zero across a release cycle in every
  environment, the string branch goes and readers become strict. Until then the meter is the
  evidence that removing it is safe, rather than a guess.

Tolerance cannot help the EF reader for mapped models (section 2), which is why the rewrite in 3.6
must be complete for everything EF maps regardless.

The object-mode `[WhizbangId]` readers on the Persistence profile follow the same rule: accept the
scalar form for rows the EF fallback path wrote under Default before 3.4, count it, remove it later.

### 3.4 An opaque document is read under the profile it was written in

**Corrected during implementation.** The data source's JSON options cannot move to the
Persistence profile: they also serve the outbox, inbox and event store `metadata` and `scope`
columns, which `WhizbangModelBuilderExtensions` maps as jsonb POCOs through a `COLUMN_TYPE_JSONB`
constant (a literal grep for `"jsonb"` misses it, which is how section 2 came to say the data
source had one consumer). Those columns are the wire's form and are read elsewhere with explicit
Default-profile options; moving the data source would have reproduced 1.1 for every envelope.

So the document column is bound to the profile it is written in, explicitly. The opaque mapping
snippet emits `.HasConversion(PerspectiveDocumentSerialization.ConverterFor<T>())` for `data`,
`metadata` and `scope`, where `PerspectiveDocumentSerialization` (runtime, `Whizbang.Data.EFCore.Postgres`)
holds the one set of options every perspective document is written and read with: the same
resolution the atomic upsert uses (union under Persistence, caller provider as fallback, registered
modifiers re-applied), cached and rebuilt only when `JsonContextRegistry.Generation` advances. The
upsert's own `_resolvePersistenceOptions` now delegates to it, so the writer and the opaque reader
are literally one options object. The data source keeps the Default profile. A generator test pins
the binding for all three columns; an integration test writes through the upsert with the data
source on the Default profile, reads through Entity Framework, and proves the Default profile could
not have read the row.

**Second correction, found by the same tests.** The generated `MessageJsonContext` facade answers
for primitive types itself (`DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, and
every other primitive) with a fixed built-in converter, bypassing `options.Converters`. Any chain
that consults a consumer's facade before the framework's contexts therefore wrote a rendering under
the Persistence profile, whatever the profile registered; the union only worked because Core's
contexts happen to register first. The facade now emits `_registeredOrBuiltIn<TValue>`, which
defers to a converter registered on the options (a factory is asked for its converter) before the
built-in one, for the primitive, nullable and list-element branches alike. The generated
`PerspectivePersistenceJsonContext.CreateOptions` is also based on the profile's own options, so
options that claim to be persistence options carry the profile's converters in every respect.

### 3.5 A mapped document: EF converts exactly what EF maps

Replace the generated `__TEMPORAL_CONVERTER_CONFIGS__` (driven by the partial discovery of 1.3)
with a model-finalizing convention in `Whizbang.Data.EFCore.Postgres` that walks every complex type
mapped to JSON, recursively and through complex collections, and applies the canonical converter to
every property whose CLR type is one of the five kinds. Inherited members, nested objects,
collection elements, `metadata` and `scope` are all reached because EF's own walk is what reaches
them. The writer (3.2, everything) and the reader (this, everything EF maps) then agree by
construction rather than by two discoveries staying in step.

Probe first, in the style of `CollectionMemberShapeProbeTests`: that a convention can reach the
properties of a `ComplexCollection` element type and set a converter there. If it cannot, keep
generator emission but derive it from a discovery that mirrors EF's walk, and add a startup
self-check that walks `IModel` and fails fast on any temporal property inside a JSON-mapped complex
type that has no conversion. Failing at startup with a precise message is the acceptable outcome;
serving a document the reader cannot parse is not.

### 3.6 The rewrite: complete for what EF maps, derived from the model, ledgered for units

**Which paths.** For a mapped model the (path, kind) list is built at startup from the same `IModel`
walk as 3.5, so it is complete by construction. For an opaque model there is no EF walk; the
generator's discovery is extended to walk base types, nested objects and collection elements, and
emits paths with array markers. A path missed there is benign for instants (3.3 reads the string)
but not for units (a tick count read as microseconds is ten times too short), so the extended
discovery must be exhaustive for Duration and Day in opaque models, and an analyzer diagnostic
refuses a Duration or Day it cannot path rather than letting it through.

**How.** One SQL function, `wh_canonicalize_temporal(doc jsonb, path text[], kind smallint,
from_form smallint) returns jsonb`, rewrites one path in one document, descending through arrays
where the path has an array marker: a string becomes the canonical number for its kind; a number
is converted from `from_form`'s unit to microseconds; anything already canonical is returned as is.
Per-table statements become `UPDATE t SET data = wh_canonicalize_temporal(data, ...), metadata =
wh_canonicalize_temporal(metadata, '{Timestamp}', ...)` for each path.

**Ledger.** `wh_perspective_forms(table_name text primary key, temporal_form smallint not null,
applied_at timestamptz not null)`. Form 1 is today's mixed-unit encoding; form 2 is microseconds
everywhere. A fresh table is created at form 2 with its ledger row in the same statement batch.
The rewrite for one table is one transaction: every path's rewrite with `from_form` taken from the
ledger (absent means 1), then the ledger upsert to 2, then commit. Interrupted mid-table, nothing
is recorded and the whole table runs again; interrupted between tables, the finished ones are
skipped. Nothing about the values is inspected to decide whether to convert.

**Placement.** Changed during implementation: the rewrite pre-phase used to run on every replica
*before* the bootstrap, so the ledger and function a bootstrap-marked migration creates did not
exist when it ran. It now runs right after the election, on the migrator or on an instance that
could not be staged, never on a waiter, still under the session-scoped schema lock and still ahead
of the DDL phase, so indexes are built against committed rows (the trap in
`ai-docs/schema-initialization-connections.md`). Migration 153 is the fifth bootstrap-marked file.

**Implemented shape (S5a, S5b).** `wh_perspective_forms(table_name, temporal_form, applied_at,
settled_at)` plus `wh_canonicalize_temporal(doc, path[], kind, from_form)` and its leaf helper, in
migration 153. `CanonicalTemporalRewrite` (runtime, `Whizbang.Data.EFCore.Postgres`) derives the
paths at startup from the two readers themselves: the EF model for a mapped document (JSON complex
properties, nested and collection ones included) and the serializer's `JsonTypeInfo` metadata for
an opaque document (properties, enumerable elements, positional record parameters). One `DO` block
per table: `to_regclass` guard, ledger read, early return when settled, one `UPDATE` per path whose
`WHERE` is a `jsonb_path_exists` predicate (renderings always; numbers only below form 2 and only
for Day and Duration), then the ledger upsert to form 2 with `settled_at` set when a pass at form 2
touched nothing. The generated per-property rewrite, `CanonicalTemporalBackfillSql` and the
generator-side discovery `From` are gone; `KindOf` stays for the index cast.

**The mixed-version window.** During a rolling update, instances of the previous release keep
writing until they are replaced. For Instant, OffsetInstant and TimeOfDay that is harmless (same
unit). For Day and Duration it is not: an old instance writes a day count or a tick count into a
table the ledger already says is form 2, and nothing can tell that row apart afterward. Two rules
follow. First, a release that changes a unit is deployed without a mixed fleet: scale to zero, deploy,
bring up; the migrator logs a Warning naming any instance of an older release still heartbeating when
the form-2 rewrite runs, which needs the release version in the instance registry if it is not there
yet. Second, and already true since the canonical form first shipped: a `bigint` expression index
rejects a rendering, so an older instance's insert into an indexed temporal fails during any rolling
update across that boundary. The docs state both rules with the migration.

**Indexes.** Every temporal index cast becomes `int8`. The Day indexes change expression, so their
generated name changes (a cast suffix), the old name is dropped explicitly, and `IF NOT EXISTS`
builds the new one.

**Cost.** The plan `temporal-backfill-blocks-readiness.md` already covers why this runs ahead of
serving and what batching and resumption it needs. The ledger here is the resumption marker that
plan lacked.

### 3.7 Framework documents are documents

`PerspectiveMetadata.Timestamp` becomes canonical by 3.2 (writer), 3.5 (reader) and 3.6
(`metadata` column rewrite). No SQL in the framework reads `metadata ->> 'Timestamp'`, so nothing
else changes. The pinned test `AFrameworkDocumentIsNotConvertedAsync` is inverted deliberately.

### 3.8 A storage-form failure is classified, counted, and stops repeating

- The perspective worker classifies a `JsonException` raised while materializing a row as
  `StoredFormUnreadable` and logs it at Error **once per (perspective, stream)** with its own event
  id and a message that names the perspective, the table, the JSON path, the token found and the
  forms accepted. Subsequent cycles for the same stream log at Debug.
- A meter, `whizbang.perspective.read_failures`, tagged by perspective and reason.
- A stream that fails materialization on consecutive cycles is parked with backoff rather than
  retried every cycle; the health endpoint reports the parked count. The park semantics belong with
  the dead-letter recovery design and are referenced from there, not redesigned here.
- The lifecycle "(continuing)" swallow already logs the exception; it moves to Error when the
  cause is deserialization.
- Found while building this: the mapped path read a document through Entity Framework's own
  integer reader, which refused a rendering the serializer's readers took and refused an
  unexpected token with a generic error nothing could classify. One reader per kind now lives in
  Core (`CanonicalTemporalReaders`); the serializer's converters and Entity Framework's
  reader/writers (set by the convention, `CanonicalTemporalJsonReaderWriters`) both call it, so
  both paths read and refuse the same values in the same words, and the classification is by
  exception type (`StoredFormUnreadable`) on both.
- Also found: why "it never stops" (1.6). A drain-path apply failure reports a cursor failure with
  no event id, which the coordinator skips, and the batched strategy then sends that same failure
  through the failure channel with the empty id as the row id. And the failure function read the
  element fields `EventWorkId`/`FailureReason` while the runtime serializes `MessageId`/`Reason`,
  so no element the runtime ever sent matched a row. Nothing was recorded, the counter the
  dead-letter decision reads never moved, no backoff was scheduled; the lease lapsed and the row
  was re-claimed. Migration 154 reads both spellings, and the worker now reports every leased row
  of a stream it cannot read through the failure channel, so the database records the failure,
  schedules the retry with backoff, and dead-letters at the threshold. That is the parking; the
  in-process registry (`StoredFormFailureRegistry`) only remembers the announcement and feeds the
  health source (`StoredFormHealthSource`, component `perspective-stored-forms`).
- Also found, by the full suite: the convention plugin rides the Whizbang options extension, and a
  lens context built by hand from a plain connection string (as four existing tests and any consumer
  reading through a pooled factory do) carries no extension, so it had no convention and read the
  stored numbers with the default reader. The generated OnModelCreating now calls
  `CanonicalTemporalConvention.Apply(modelBuilder)` after the consumer's extension, the same walk
  over the model as built, as explicit configuration; the finalizing convention stays for a
  hand-written context that carries the extension, and the two agree.
- Also found, by the sample integration jobs on the PR's first CI run and reproduced locally (two
  failures in six runs; the base branch clean in four): the generated message facade cached the
  metadata it creates by type alone, per thread. Metadata is bound to the options it was created
  for, and a property's converter is chosen from those options, so once the wire profile had asked
  for a `DateTime` on a thread, the persistence profile received a date bound to the wire's options
  and wrote a rendering into a document whose index casts the key to `bigint` (`22P02`). Before
  this design the per-model modifiers hid it by rewriting the cached metadata's converters in
  place. The facade's cache is now keyed by options (a weak table, per thread), pinned by a Core
  test that asks in both orders and by a generator test.
- Found in the first rollout of the alpha, fixed in S8a and S8d: two instances of one service
  started in the same second. The elected migrator's rewrite phase took the schema-init key with a
  single try-lock and skipped at Debug when it lost, on the assumption that the holder was another
  rewriter; the holder was the sibling's bootstrap or DDL transaction, which converts nothing, and
  the sibling was then staged as a waiter, which never rewrites. Every table of that schema stayed
  in the old form with no line above debug level. Separately, a migrator killed mid-rewrite by a
  rolling restart left the remaining tables unconverted, and both replacements were waiters whose
  deferral ended in a takeover that ran the DDL loop but not the rewrite, because the rewrite sat
  before the wait behind a "not a waiter" guard. The phase now waits for the key at transaction
  scope with a savepoint per table, and the takeover path runs the same rewrite body first.
- Found in the first rollout, documented in S8c: a consumer-owned trigger cast a temporal key's
  text to `timestamptz`. The rewrite's update of that table fired it and failed with `22008` on the
  canonical number, which the phase reported as a failed table while converting the rest; the same
  trigger would have failed every later write into that table. The framework cannot know such an
  object exists; the rule and the transition-safe `CASE` shape are in the docs.
- Found in the first rollout, no change: a table with no model in the running binary (a perspective
  removed from the source, its table left behind) is correctly not named by the rewrite, since nothing
  reads it; and a verification regex that scans document text counts a rendering inside a
  string-typed member that happens to hold serialized JSON, so verify by path, never by text.
- Found in the first rollout, fixed in S8b: the pass was silent on success, so an operator could not
  tell "converted" from "never ran" without querying the ledger. Each table now reports its outcome.
- Follow-up, not in this PR: the outbox and inbox failure functions read `FailureReason` the same
  way, so `failure_reason` on those rows has always been Unknown. Different tables and functions
  with their own drift-pinned tests, and no bearing on stored forms; it deserves its own change.
- Follow-up, not in this PR: a drain-path apply failure for any other reason still reports only
  the empty-id cursor failure, so those rows still park nothing. Changing that changes retry
  semantics for every consumer's apply failure (rows would dead-letter after the configured
  failures where today they retry until the source is fixed), which is a decision to make on its
  own, not a side effect of this one.

### 3.9 Tests: the matrix that was missing

Every cell below is a test, and the row and column sets are enumerated in code so a new kind or
path cannot be added without a cell.

- **Writer x reader x form x kind.** Writers: Path 1, EF SaveChanges on a mapped model, EF
  SaveChanges on an opaque model. Readers: EF mapped, STJ opaque, SQL extraction with the `int8`
  cast. Forms: string rendering, form-1 number (days, ticks, microseconds), form-2 number. Kinds:
  all five, nullable and not, `infinity` and `-infinity` and default values.
- **Placement.** Top-level, inherited from a base class, nested object, complex-collection element,
  `PerspectiveMetadata.Timestamp`.
- **Profile pins.** The Default profile still writes a rendering (existing); the Persistence
  converters do not reach Default (existing, renamed); the generated data source uses Persistence.
- **Opaque plus `[WhizbangId]`** round-trips, both object and scalar stored forms.
- **Ledger.** Run twice is a no-op; interrupted between tables resumes; interrupted mid-table
  re-runs that table exactly once; fresh table starts at form 2; unit conversion is exact at the
  sentinels.
- **Rewrite through arrays** with a fixture built by serializing a real model, not hand-written JSON.
- **Index rename** on cast change: old dropped, new present, expression uses `int8`.
- **Convention probe** (3.5) or the startup self-check, whichever ships.
- **Loudness.** One Error per stream, the message names the path, the meter increments, the stream
  parks.

### 3.10 Pins revised

| Test | Change |
|---|---|
| `AModifierDoesNotReachAnotherProfileAsync` | kept in spirit; becomes "a Persistence converter does not reach the Default profile" |
| `AFrameworkDocumentIsNotConvertedAsync` | inverted: the framework document is converted like any other |
| `TheTransportProfileStillWritesARenderingAsync` | kept as is |
| `ATemporalPropertyNotNamedIsLeftAloneAsync` | removed with the per-model API; replaced by placement tests |
| `CanonicalTemporalFormatTests.ADateOnlyIsDaysSinceTheEpochAsync`, `ATimeSpanIsItsTickCountAsync` | replaced by the microsecond forms |

## 4. Rollout

One-way, one release. Readers accept old forms; writers emit the new one; the ledger gates the
unit rewrite; the rewrite runs once per table at startup ahead of serving. A consumer changes
nothing. An opaque document stops failing the moment the new release starts, before any rewrite,
because 3.3 and 3.4 do not depend on it.

Docs to update on the site: the canonical stored-forms table in the perspectives section and the
migrations page (the ledger, the one-way note). Public API removal: `CanonicalTemporalJsonConverters.ApplyTo`.

## 5. Not in this design

- `[Indexed]` on nested paths (tracked in `lens-full-index-coverage.md`).
- General park semantics for a stream that fails for any other reason (dead-letter recovery plan).
- Making an older release read canonical rows.

## 6. Progress

Kept current as slices land. One branch, one PR, one release.

| Slice | What | Status |
|---|---|---|
| S0 | Probes: an EF convention and an STJ options converter reach inherited, nested, collection-element and metadata temporals | done |
| S1 | One unit (microseconds for all five kinds); readers strict about units, tolerant of renderings, metered | done |
| S2 | Generator casts: `int8` for every kind, `HasConversion<long>` everywhere | done |
| S3 | EF model-finalizing convention replaces generated `HasConversion` | done |
| S4a | Profile-global Persistence converters; per-model modifiers and `ApplyTo` removed; lenient readers scoped to Default | done |
| S4b | Opaque documents bound to the Persistence profile through `PerspectiveDocumentSerialization`; generated facade defers to registered converters | done |
| S4c | Object-mode identifier readers accept the scalar form, counted by type (`StoredFormFallbacks`) | done |
| S5a | Migration 153: `wh_perspective_forms` ledger and `wh_canonicalize_temporal` | done |
| S5b | Runtime rewrite derived from the EF model and serializer metadata, one ledger-gated transaction per table, wired after the election | done |
| S5c | Fresh tables recorded at form 2 (settled); Day index renamed for its new cast and the old one dropped; mixed-fleet warning | done |
| S6 | Loudness: classified `StoredFormUnreadable` error once per stream, meter, parking; one reader per kind on both paths; migration 154 | done |
| S7 | Docs: stored-forms table, migrations page, ai-docs, code/tests/docs links (docs site PR whizbang-lib.github.io#619) | done |
| PR | #770 merged to develop; alpha published; consumer pinned and deployed | done |
| Rollout | First deployment of the alpha: readers tolerant everywhere, most tables converted; three findings below, two of them framework defects fixed in S8 | done, with findings |
| S8a | Rewrite waits for the schema lock instead of skipping (transaction scope, savepoint per table, warning when the budget runs out); a two-instance start had left every table unconverted with nothing above debug level | done |
| S8b | Per-table outcome (converted with count, settled, absent) as notices relayed at Information, and a one-line pass summary | done |
| S8c | Consumer-owned objects over temporal keys documented: read the number, the transition-safe `CASE` shape | done |
| S8d | A waiter that takes over from a dead migrator runs the rewrite before the DDL, through the body the migrator runs | done |
| PR 2 | S8 against develop; CI, gate, docs site PR #619 updated | in review |

## 7. Decisions requested

1. Microseconds for all five kinds, Day at midnight UTC, Duration truncated to 1 us (3.1).
2. Profile-global Persistence converters replacing per-model modifiers, and the data source on
   the Persistence profile (3.2, 3.4).
2a. Reader tolerance of renderings as a transitional, metered feature removed in a later release
   once the fallback meter reads zero; strict about units from the start (3.3).
2b. A unit-changing release deploys without a mixed fleet, and the migrator warns about older
   instances still heartbeating (3.6).
3. An EF convention replacing generated `HasConversion`, probe first, self-check as the fallback (3.5).
4. A form ledger and a SQL rewrite function that walks arrays (3.6).
5. `PerspectiveMetadata.Timestamp` canonical (3.7).
6. The loudness scope in 3.8, including parking a stream that keeps failing.
