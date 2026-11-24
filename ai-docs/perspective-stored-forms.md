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

A recursive type (a node holding a collection of itself, or two types holding each other) is one of those members
(#2112). Entity Framework maps it one level deep and drops every level below on write with no error anywhere, so the
discovery walks with two sets: the types already judged (keeps the walk finite) and the types on the current path (a
return to one is a cycle). One set answering both questions is what hid the recursion. `CollectionMemberShapeProbeTests`
measures the one-level mapping; if a future EF maps the whole depth, it fails and the routing is re-measured.

Rows an earlier release wrote on the mapped path hold only the first level; nothing rebuilds them automatically (owner
decision). `RecursiveDocumentRebuildNotice` runs first in the stored-form phase, before the start's migrations: when the
migration ledger exists and lacks `361_RecursiveDocumentsStoredWhole` (a marker migration that changes nothing), each
perspective in the generated `GetRecursivePerspectives()` whose table holds a row is logged once at Warning, event id
2112, to be rebuilt from its events. A circular model (a recursion storing a collection and a single reference back up
the document) is WHIZ201, a build error (`CircularReferenceDiscovery`); an object cycle it cannot see fails the write with
the serializer's cycle detection.

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

## App-declared stored-form migrations (#986)

An app whose model changed shape declares how its stored documents change: `[StoredForm(Previously,
PreviousName, DefaultWhenMissing)]` on the property, `[StoredFormRemoved("Path")]` on the type, or an
`IStoredFormMigration<TModel>` (raw SQL, stable `Name`). `StoredFormDiscovery` (Generators.Shared) walks
the model (own, inherited and nested-object properties; a declaration inside a collection element is
WHIZ832, an ungeneratable one WHIZ830, a custom migration that never runs WHIZ831) and the EF generator
emits `GetStoredFormMigrations()` as calls to `StoredFormMigrationSql.Generated(..., StoredFormStep.X)` and
`.Custom(...)`: the SQL lives in the runtime library, not in generated strings.

- They run in `CanonicalTemporalRewritePhase` **before** the temporal rewrite (a renamed temporal key is
  then converted in the same pass), one savepoint each, blocking with the shared SQLSTATE `WH980`.
- Journal `wh_stored_form_migrations` (migration 176, bootstrap region). A generated migration settles on
  the first pass that changes nothing (so a converting pass is followed by one more, catching rows an older
  release wrote during a rollout); a custom one settles on its first run. Settled means one lookup, no scan.
- `StoredFormMigrationSql.DeclareAsync` records every declaration as pending **outside** the phase
  transaction. Inside it, the declaration's write would assign an xid and make the #949 fence wait on
  every first start for nothing. A settling pass does write (its journal row), so the fence may wait once
  there, like the temporal ledger's settle.
- Physical fields: a type change retypes the column (`RetypeColumn`), a rename renames it; an enum target
  is left to `EnumColumnRewriteSql`; a Split model has no document step.
- Status: generated `GetStoredFormMigrationStatusAsync(dbContext)` and `whizbang stored-forms status`.
- Custom order (#1007): `IStoredFormMigration.Order`, optional (a default interface member, `=> 0`, so migrations
  written before it compile and keep class-name order). A stated one is read by the generator as a compile-time
  constant (a non-constant one is WHIZ831 and the migration is not emitted). Custom migrations run after the table's
  generated ones by `Order`, class name breaking a tie; WHIZ833 only when two migrations of a table STATE the same
  order (two defaults are silent). `Order` over `DependsOn`: a number is decided and checked
  at build time; a dependency list needs missing-name and cycle checks and means nothing across tables.
- Stale-cast indexes (#1007): a type change gets `StoredFormStep.ReplaceIndex` steps, ahead of the conversion, for the
  field's own ordered `[Indexed]` index and each composite over the key. Each drops the index only when it is the
  schema's (its derived name AND a definition over `(data ->> 'Key'::text)`) and casts the key to a type other than
  the new one. `StoredFormIndexRebuild.ApplyAsync` then builds it `CONCURRENTLY` after the phase commits (outside any
  transaction), dropping an invalid leftover first and after a failure; the schema pass is the fallback.
- Parked streams (#1007): every generated migration ends with `StoredFormStep.RetryParkedStreams(perspectives)` and
  every custom one passes the table's perspectives, so a pass that changed something sets `scheduled_for = NOW()` on
  that table's unleased failed rows with reason SerializationError. The failure count stays.

Tests: `StoredFormMigrationTests`, `StoredFormMigrationSqlTests`, `StoredFormMigrationGenerationTests`,
`StoredFormAttributeTests`, and the end-to-end recovery (at once, no clock moved) in
`StoredFormScalarMismatchWorkerTests`.

## Declared JSON names (#1874)

Releases before #920 wrote every property under its C# name; now the generated message context writes the declared
`[JsonPropertyName]`. Where the two differ only in case (`"X"` stored, `"x"` declared), EF's mapped reader matches
exactly, finds nothing and reads the default with no error. Three pieces, one per reader:

- **Rewrite.** `DeclaredKeySpellingPass` (Data.Postgres) runs first in the EF stored-form phase (before the
  app's stored-form migrations and the temporal rewrite, which address declared names) and in the Dapper schema pass
  after the perspective tables exist. Shapes come from the readers: `DeclaredKeyShapes.ForModel` (EF model for mapped
  columns, serializer metadata for opaque ones) and `DeclaredKeyTable.ForSerializedModels` (Dapper, model types from the
  generated `PerspectiveDocumentModels.Register(table, typeof(Model))` module initializer). The scan walks the PK in
  chunks (`id > $1 ORDER BY id LIMIT n`), a `jsonb_path_exists ... keyvalue() ? (@.key like_regex ... flag "i" && @.key != ...)`
  predicate returns only candidate rows, the C# rewrite (`DeclaredKeySpellingDocument`, JsonNode, values moved as
  parsed) produces the new text, and the UPDATE is conditioned on `xmin`. Conflicts (both spellings, different values;
  a key matching two declared names) leave the row whole and end the pass with `StoredFormConversionBlockedException`.
- **Settle.** `SettleAsync` (EF: after the stored-form phase, so `DefaultWhenMissing` has run) logs rows missing a
  required member at Error and writes `declared-names:<table>:<hash>` to `wh_stored_form_migrations`, settled when the
  pass changed nothing and nothing is missing. The hash is over `DeclaredKeyShape.Describe()`, so a model change rescans
  once. A table this release creates empty is settled on its first check (Dapper) or its second start (EF).
- **Reads.** `JsonContextRegistry.CreateCombinedOptions` sets `PropertyNameCaseInsensitive`: events are history and are
  not rewritten, so the reader tolerates the old case.
- **Required, rows only (owner decision 2026-10-29).** The message JSON context marks `required` / `[JsonRequired]`
  members `IsRequired` (unless the type admits null; the options omit nulls when writing) only on types whose
  `RequiredMemberReading` is `Refused`: a perspective model and the types only perspective models reach. Events,
  commands, composites and other serializable types, and everything they carry, are `Tolerated`: a missing member
  reads as its default, so a replay never fails because a later change added `required` (history is read tolerantly
  and upcast; the compiler guards new messages). Reachability (`_requiredMemberReadings`) walks properties,
  collection elements, dictionary values and polymorphic derived types from two root sets: perspective models (found
  by either discovery path, read before dedup) and every other discovered message. A type reached from both is
  `Reported`: one JsonTypeInfo per type serves both readers and history wins, so it is read tolerantly, and the
  generated initializer calls `DeclaredRequiredMembers.Register(typeof(T), "<json names>")` so
  `DeclaredKeyShape.FromSerializer` still marks the member required and the settle step still reports rows missing it.
  A refused init-only member gets a setter that throws: System.Text.Json refuses a required property with no setter
  even when a constructor parameter fills it, and never calls the setter of a property a parameter fills.

Not handled: a stored key that differs by more than case (`Points` declared `pts`): that is a rename and needs
`[StoredForm(PreviousName = ...)]`. EF's mapped reader cannot refuse a missing required key, and a type shared with
an event is read tolerantly on every path; the settle step's Error is the only signal there.

## Dictionary members: an array of key/value objects (#1911)

Entity Framework's JSON mapping takes a dictionary for a navigation and refuses the model. Every
`Dictionary<K,V>`, `IDictionary<K,V>` and `IReadOnlyDictionary<K,V>` in a perspective document, at any depth, is
stored as `[{"Key":..,"Value":..}]` in the dictionary's order (a jsonb object would sort its keys). Pieces:

| Piece | What it does |
|---|---|
| `DictionaryMemberDiscovery` (Generators.Shared) + `PerspectiveSchemaGenerator` | Emits `DictionaryMemberForms.Register<K, V>()` in the schema module initializer for every closed dictionary type a model holds (members, nested objects, collection elements, dictionary values) |
| `DictionaryMemberForm<K,V>` (Core) | The one reader/writer: keys and values through the persistence options' metadata (a temporal value is the canonical number), reads the array or an earlier release's object (counted on `whizbang.perspective.dictionary_form_fallbacks`), refuses a duplicate or missing key |
| `DictionaryMemberForms` (Core) | The registry, and a resolver at priority 2000 on the persistence profile resolving a registered type to `KeyValueArrayJsonConverter` (the atomic upsert's writer) |
| `DictionaryMemberTypeMappingSourcePlugin` / `DictionaryMemberTypeMapping` (EF) | A type mapping, not a convention: EF asks the type mapping source while discovering a complex type's members, which is where it fails. JSON reader/writer, a stored-form converter and a deep comparer. Rides `UseWhizbangFunctions` |
| `DictionaryMemberConvention.Apply` (EF), called by the generated `ConfigureWhizbang` | A context built without the options extension (consumer-registered, hand-built lens, most test contexts) has no plugin, and one dictionary anywhere in an assembly's perspectives then fails every such context's model. The generator also emits `DictionaryMemberForms.RegisterMember(typeof(Owner), "Name", typeof(Member))`; `Apply` adds each registered member inside a document as a property with the same mapping. Already mapped or ignored members are left alone |
| `DictionaryMemberTranslator` (EF) | `ContainsKey(k)` becomes `data @> {"Path":[{"Key":k}]}` (GIN-answerable) or `member @> [...]` inside a collection element; `m.D[k]` becomes `jsonb_path_query_first(member, '$[*] ? (@.Key == $k)', ...) ->> 'Value'` cast to the value type. The key is sent through the mapping's own key converter, so any key type compares as stored. Temporal and structured values, non-constant keys and other methods are left for EF to refuse |
| `PhysicalJsonbContainmentRewriter` (EF) | A promoted jsonb dictionary column of a registered type is written as pairs too, so `col[key] == v` / `col[key].Contains(v)` build `col @> [{"Key":key,"Value":...}]`; an element of a list of such dictionaries merges its pairs. An unregistered dictionary type keeps the object form on both sides |
| `BaseUpsertStrategy.PhysicalColumnValue` | Takes a jsonb column's converter from its type mapping when the column names none, so the dictionary mapping's stored form reaches the column |
| `DictionaryPairsRewrite` (EF) + migration 544 | The stored-form phase rewrites object-form dictionaries (opaque documents and promoted jsonb columns) once per table, before the temporal rewrite; key kinds string, number and enum-name map; journaled `dictionary-pairs:<table>:<hash>` in `wh_stored_form_migrations`, settled on a pass that converts nothing; included in `StoredFormSettlement`. A date or structured key is left for the reader |

Not handled: `SortedDictionary`, `ImmutableDictionary`, `ConcurrentDictionary` and custom dictionary types (not
registered, so EF still refuses them); the Dapper driver writes with the options the app gives it and has no rewrite.

## Strong ids: a UUID string (#2518)

A `[WhizbangId]` in a perspective document is stored as its UUID string, like a `Guid` and like the transport writes
it, not as the object `{"Value":".."}` Entity Framework discovers for a struct. Pieces:

| Piece | What it does |
|---|---|
| `WhizbangIdForms.Register<TId>(guid => new TId(guid))` (Core), emitted by `WhizbangIdGenerator` in each id's module initializer | The registry of id types, with the unchecked load (the constructor) and a visitor so the EF side builds typed converters without reflection. `TryGet` runs a missing id's module initializer (`RuntimeHelpers.RunModuleConstructor`) and looks again, so a model built before any code of the ids' assembly has run still maps them as scalars (pinned by `WhizbangIdModelTimingTests`) |
| `WhizbangIdTypeMappingSourcePlugin` / `WhizbangIdTypeMapping` (EF) | A type mapping, as for dictionaries: EF asks while discovering a complex type's members, which is where it would make the id a complex type. `uuid` store type, `TId <-> Guid` converter through the constructor, and `WhizbangIdJsonReaderWriter<TId>` (writes the string, reads the string or the object form, counted). Rides `UseWhizbangFunctions` |
| `WhizbangIdConvention.Apply` (EF), called by the generated `OnModelCreating` | A context without the options extension (a pooled factory registered by hand) still discovers ids as complex types; `Apply` removes each and adds the member back as a property with the id mapping. A collection of ids is left as discovered |
| Persistence JSON context (`_{T}DocumentConverter`) | The atomic upsert's writer: the UUID string; reads either form, counting the object form on `whizbang.perspective.identifier_form_fallbacks` (`StoredFormFallbacks.ObjectFormIdentifierRead`) |
| `JsonbContainmentRewriter` / `JsonbContainment` (EF) | An id equality goes through the `Guid` marker: the member unboxed through `object` (stripped around the JSON member at translation), the value converted to its Guid; `Emit` drops that conversion and gives the parameter the member's id mapping, so the document is `jsonb_build_object('OrderId', @id)` and the GIN index answers it. For an id, `Emit` and `EmitSet` add a second containment arm for the object form (`{"OrderId":{"Value":@id}}`, and `jsonb_strong_id_containment_set` for `in`); the index answers both. `StoredFormIsNatural` accepts the id-to-Guid converter |
| `StrongIdToleranceRewriter` (EF), in `ContainmentPostprocessor`, last (after the temporal rewriter and, in `TranslatedTree` mode, `ContainmentSqlRewriter`, whose `JsonbContainmentSql.TryBuild` adds the object-form arm itself) | Every id read in SQL takes either form: a `JsonScalarExpression` with the id mapping becomes `CAST(CASE WHEN t LIKE '{%' THEN jsonb_extract_path_text(CAST(t AS jsonb), 'Value') ELSE t END AS uuid)` over the text `t`; a `jsonb_to_recordset` id column is read as text and then the same way; `ids @> to_jsonb(@id)` gains `OR ids @> jsonb_build_array(jsonb_build_object('Value', @id))` |
| `StrongIdScalarRewrite` (EF) + `BatchedRewriteRunner` + `StrongIdScalarRewriteStep` + migration 557 | Object-form ids (EF-mapped documents, opaque jsonb columns, dictionary keys and values) are rewritten one primary-key batch per statement, each batch its own transaction under the schema lock (`pg_try_advisory_xact_lock`; a held lock ends the run), the journal row locked `FOR UPDATE` and carrying `resume_key` and `pass_rows_converted`. Rewrites take batches in turn; a failed batch rolls back and the others go on. A pass reaching the end settles the row if it converted nothing, else a new pass starts. Startup runs one slice after the phase (`BatchedRewriteBudget.ForStartup`, four fifths of the command timeout); the maintenance step carries on (`ForMaintenance`, 30 s) and remembers a settled context per process. Held while `FleetVersions.OlderLiveLibraryVersionsAsync` names a live instance of an older library version (the old EF mapping throws `Invalid token type: 'String'` on the new form). Not in `StoredFormSettlement`: the fast path does not wait on it |
| `WhizbangIdFilterHandler` (HotChocolate) | Compares the id itself everywhere; the inner `Value` it used to read in a document is no longer a mapped member |

Correctness does not wait on the rewrite: a table it has not finished, or a row an instance of an earlier release writes
during a rolling deploy after the table settled, is read and queried in either form (pinned by
`Whizbang_EveryIdQueryFindsARowStillInTheObjectFormAsync`, the rewrite's rolling-deploy test and
`EqAndIn_FindARowStillInTheObjectForm_ThroughTheDocumentIndexAsync`). The rewrite is what lets that tolerance go in a
later release. Hand-written SQL that matched `{"Value": ...}` has to match the string.

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
it renders and counted on `whizbang.perspective.temporal_form_fallbacks{kind}` (identifiers read in the object form
on `whizbang.perspective.identifier_form_fallbacks{type}`), announced once per kind at Warning by
`StoredFormFallbacks`. The rendering branch is removed once the counter reads zero across a release
cycle. Removing it earlier makes a row the rewrite did not reach a stopped feature again.

## When a row cannot be read

`StoredFormUnreadable.TryClassify(exception, out failure)` finds a reader's `JsonException` anywhere
in a chain of wrappers or an aggregate and carries the path and the message.

A mapped document is read by Entity Framework's own materializer, which reports a scalar of the wrong
JSON type (a number where the property is a string) as the reader's bare `InvalidOperationException`,
with no path and no `JsonException` anywhere in it. `EFCorePostgresPerspectiveStore` therefore catches a
failed row read, and `MappedDocumentReadFailure` walks the row's stored documents against the model
Entity Framework read them with. The first value the property mapped at its place cannot take is raised
as a `JsonException` carrying the path (`$.Status`), with the original inside it; when the walk finds
nothing, the original is rethrown as it was. Temporals are left to their own readers, which already
refuse with a `JsonException`. The opaque path and the Dapper store read with System.Text.Json, whose
refusal is already a `JsonException` with the path.

The perspective worker, in both the drain path and the channel path:

1. records the failure in `StoredFormFailureRegistry` and logs event id 65 at Error once per
   (perspective, stream), event id 66 at Debug after that, until the stream reads again;
2. counts `whizbang.perspective.read_failures{perspective_name, reason=stored_form_unreadable}`;
3. reports every leased row of the group through `IFailureChannel` as `WorkCategory.PerspectiveEvent`
   with `MessageFailureReason.SerializationError`, so `process_perspective_event_failures` records the
   failure, schedules the retry with backoff and the dead-letter check reads the counter. The function
   counts one failure per lease: a claim re-offers a leased row, so one stream can be drained twice at
   once and both drains report the same lease's failure; the second finds the row failed and not leased
   again, and changes nothing (issue #987);
4. the `perspective-stored-forms` health component (`StoredFormHealthSource`) reports Degraded with the
   count while any are remembered.

The inbox lifecycle "(continuing)" swallow logs at Error instead of Warning when the cause is a
deserialization failure (event id 74).

## Three defects found underneath

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
  `failure_reason` on those rows was always Unknown until migration 156 made them read both too.
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
`FleetVersionsTests`, `CanonicalTemporalFunctionTests`, `PerspectiveFailureCounterSqlTests`,
`MappedDocumentReadFailureTests`, `StoredFormScalarMismatchReadTests`, and
`StoredFormScalarMismatchWorkerTests` (a scalar of the wrong type through the real claim loop, worker
and runner: classified, parked with backoff, dead-lettered at the threshold, other streams flowing,
Degraded health, recovery after the value is corrected, and many poisoned streams not starving
healthy ones). Dapper: `DapperPostgresPerspectiveStoreTests`.
Generators: `CanonicalTemporalConfigurationTests`, `CanonicalTemporalRewriteWiringTests`,
`JsonIndexGenerationTests`.
