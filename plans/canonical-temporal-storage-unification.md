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

### 3.3 One reader rule: a reader accepts every form the framework has ever written

Each canonical converter's `Read`:

- `Number`: the canonical unit (3.1);
- `String`: the rendering (ISO 8601, and the `infinity` / `-infinity` and missing-offset cases
  `LenientDateTimeOffsetConverter` already handles), because a row the rewrite has not reached, or a
  document written by an older release, is still a document;
- anything else: a `JsonException` naming the CLR type, the token found, and the two forms accepted.
  STJ appends the JSON path.

This is insurance, not the mechanism. It cannot help the EF reader (section 2), and it cannot tell a
day count from a microsecond count, which is why 3.6 has a ledger.

The same rule applies to the object-mode `[WhizbangId]` readers on the Persistence profile: accept
the scalar form too, for rows the EF fallback path wrote under Default before 3.4.

### 3.4 An opaque document is read under the profile it was written in

Both data-source emission sites in `EFCoreServiceRegistrationGenerator` change to
`ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence))`.
That is the whole of the fix for 1.1. A generator test pins the profile by name so it cannot drift
back.

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

**Placement.** Unchanged: the rewrite pre-phase runs under its own lock, ahead of the DDL phase, so
indexes are built against committed rows (the trap in `ai-docs/schema-initialization-connections.md`).

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

## 6. Decisions requested

1. Microseconds for all five kinds, Day at midnight UTC, Duration truncated to 1 us (3.1).
2. Profile-global Persistence converters replacing per-model modifiers, with tolerant readers,
   and the data source on the Persistence profile (3.2, 3.3, 3.4).
3. An EF convention replacing generated `HasConversion`, probe first, self-check as the fallback (3.5).
4. A form ledger and a SQL rewrite function that walks arrays (3.6).
5. `PerspectiveMetadata.Timestamp` canonical (3.7).
6. The loudness scope in 3.8, including parking a stream that keeps failing.
