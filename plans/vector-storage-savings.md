# Vector storage savings, lossless (#2333)

Status: in progress. V1 (#2624) merged. V2 (#2625, migration 720) and V3 (#2626, migration 721) merged (#2641). V4
(#2627, migration 750) and V5 (#2628) implemented on feat/2627-pack-vectors (2026-11-28). Direction and decisions by the owner on 2026-11-21, 2026-11-25 and 2026-11-27.

V2/V3 as built, where they differ from the design below: the switch is the journal row itself
(`wh_stored_form_migrations` `wh_event_body:packed-vectors`, written when the gate opens, before the first pack),
not a separate `wh_settings` key, so nothing can pack unjournaled; the reader marker is `stored_form_level` 2 in the
heartbeat metadata (the 537 precedent), not a new column; the journal records the library version only, so an
application rolled back on the same library is not stood down; the paths are written by the `vector-packing-gate`
maintenance step before it asks the gate; exports read through the SQL `wh_event_body_wire_form` (redelivery
selection and trace store), found by a machine-derived inventory test. Owner decision 2026-11-27: `List<float>` and
`ReadOnlyMemory<float>` are vectors only when marked `[VectorField]`.
Release: 1.0 (Release 1.0 Planning board). Part of the storage program ([storage-reclamation.md](storage-reclamation.md));
related to #2438 (shared event bodies stored once by content hash). Tracking: #2333 (question, accepted); each slice
below is drafted to be filed as its own issue.

## Decisions

| Date (ET) | Decision | Source |
|---|---|---|
| 2026-11-21 | No half precision. Full fidelity is required; find lossless savings instead. | [owner comment](https://github.com/whizbang-lib/whizbang/issues/2333#issuecomment-7066157391), restated with the measurement in the [options comment](https://github.com/whizbang-lib/whizbang/issues/2333#issuecomment-7066289806) |
| 2026-11-25 | **Shared vectors in read models are opt-in per `[VectorField]`.** They add a table and a join, so the developer chooses them. | Owner, given to the planning session |
| 2026-11-25 | **Binary vectors in event bodies are on by default, with a per-property opt-out**, gated so a rolling deploy can never write a form an older instance cannot read. Default-on was proposed by the implementer and accepted by the owner. | Owner, given to the planning session |

The 2026-11-25 decisions are not yet recorded on the issue thread; the thread ends at the options comment and a claim
note. They should be posted there when the slices are filed.

Out of scope: half precision or any lossy form (decided against); a compact search index with exact re-rank (option 3 in
the options comment), which adds space rather than saving it and is a separate question if search speed is wanted.

## Measurement (where the space goes)

Read-only, on a development database of a consumer deployment with 3,072-dimension embeddings, from the options comment,
and from [event-body-locator.md](event-body-locator.md) for the event counts:

| Finding | Value |
|---|---|
| Vector read models, total | about 3.5 GB on one development server (issue body) |
| Vector share of each read-model row | about 95%, stored raw as `vector(3072)` at 12,292 bytes; the JSON document does not repeat it |
| Vector index | none; search is an exact scan |
| Exact duplicate vectors, read model A | about 90% of rows repeat a vector already stored; one vector appears on up to about 800 rows |
| Exact duplicate vectors, read model B | about 39% of rows |
| Duplicates by text versus by vector | more distinct vectors than distinct contents: the same text can have more than one vector, so dedupe must key on the exact vector, never the text |
| Vector events in the event store | about 24 KB per event (vectors as JSON floats, about twice the raw size); 128,088 created events, average stored body about 24.6 KB |
| Vector events' share of `wh_event_body` | more than half, and larger than the read models |

What the two levers save, estimated from those numbers (each slice re-measures):

| Lever | Per item | Where it applies |
|---|---|---|
| Event bodies, packed (V1, V2, V4) | about 24 KB of JSON floats becomes about 16.4 KB of base64 (3,072 x 4 bytes x 4/3): about a third of each vector event body | every vector event, by default |
| Read models, shared (V6 to V8) | a repeated vector costs a 32-byte hash and a reference instead of 12,292 bytes | read model A: about 90% of its vector bytes; read model B: about 39% |

The options comment estimated "about half the size" for binary event bodies: that is the raw-byte figure. The form below
keeps the vector inside the JSON body as base64, which lands at about two thirds. Storing raw bytes beside the body
(a `bytea` column with a placeholder in the JSON) would reach half, but every read of a body in SQL or C# would then
reassemble it, on the hottest read paths. It is recorded here as a possible later step, to measure only after V4.

Shared read-model vectors also make exact search cheaper: the distance is computed once per distinct vector, so read model
A's exact scan reads about a tenth of today's vector bytes.

## Design

### Event bodies: packed vectors (default on)

- **What is a vector property.** On an event type, a property of type `float[]`, `ReadOnlyMemory<float>` or
  `IReadOnlyList<float>`, at the top level or inside a nested object. `double[]` and every other numeric type are never
  packed: their values do not fit binary32 exactly. A property opts out with an attribute (working name
  `[StoredAsJsonArray]`). Arrays of objects that contain vectors are not packed in 1.0; the generator reports them as
  info so nothing is skipped silently.
- **The packed form.** In the stored body only, the JSON array becomes one JSON string: base64, without line breaks, of
  the big-endian IEEE-754 binary32 bits of each element in order. A reader tells the forms apart by token type (array or
  string). Lossless by construction: the C# writer emits the shortest round-trip text for each `float`, jsonb keeps that
  text exactly as `numeric`, and `numeric::float4` rounds it correctly back to the same bits.
- **Where it is packed: in SQL, at the stored body, never on the wire.** The outbox and inbox keep the wire form, and the
  transport carries JSON floats as it does today, so another service (in another database, on any version) never sees
  the packed form. Packing happens where the body is written to `wh_event_body`: both emit chains (`_emit_event_store_chain`,
  latest in `573_StreamsAreVersionedInEmissionOrder.sql`, and `_emit_event_store_chain_for_inbox`, latest in
  `574_ReceivedStreamsAreVersionedInReceiveOrder.sql`) and `wh_append_event` (latest in
  `537_StoredMetadataLeavesOutWhatTheRowHolds.sql`). This follows the slim stored metadata precedent (#1725, migration
  285): the stored form differs from the wire form, and every write path applies it in SQL.
- **Which paths.** The generator emits, per event type, the JSON paths of its vector properties (relative to the payload,
  using the serializer's property names). A startup step writes them to a new table (working name
  `wh_event_vector_paths (event_type, path)`), the way message associations are written. The emit chains pack only when a
  stored event's type has a path, behind a per-batch flag like `v_any_collective`, so an ordinary event runs nothing new.
- **Readers.** The message JSON context generator (`src/Whizbang.Generators/MessageJsonContextGenerator.cs`) attaches a
  converter to every vector property that reads either form and always writes the array. Every C# read of a stored body
  (live reads, replay, rebuild, archive read-through) goes through it.
- **Exports.** Anything that sends a stored body off the database to another service (an integrity repair that copies
  an origin's events, a foreign copy) sends the wire form: it re-serializes through C#, which writes arrays.
- **History.** Bodies stored before packing was enabled are packed by a resumable maintenance pass (V4), journaled as a
  stored-form conversion; the space returns to the operating system through the table rewrite seam (WS-4).

### The rolling-deploy gate

Four layers, so no instance can meet a packed body it cannot read:

1. **Readers ship in the same release, and packing exists only in that release's migration.** The packing SQL is defined
   by a new migration. Every pending change from a newer version is breaking, so the standby handshake
   (`operations/startup/rolling-upgrades`) stands every older instance by before that migration commits and shuts it
   down after: no older instance is serving when the first packed body is written.
2. **A runtime switch that opens only when every live instance can read.** Each instance whose build reads packed
   vectors records a reader marker on its instance record at registration (a new column added in the Preflight bootstrap
   region, as 360 did for `incompatibility_override`). The emit chains pack only while the `wh_settings` key (working
   name `event_vector_packing`) is `on`. The maintenance tick sets it `on` only when every live instance in
   `wh_service_instances` carries the marker. This covers deployments where the handshake is bypassed
   (`Whizbang:Startup:OverrideIncompatibility`) and instances started without the stage.
3. **The switch latches, and the rollback gate guards the latch.** Once packed bodies exist, turning the switch off does
   not unpack them, so the switch never turns off by itself. The first packing records an irreversible stored-form
   conversion with its library and application version (the journals of #2131), so the rollback gate (#2022) stands down
   an older build that starts afterward, with a reason that names packed vectors. An instance that overrides that verdict
   is told what it cannot read.
4. **The wire never changes.** Other services, and any consumer of the transport, see JSON floats. A downgrade that is
   planned runs the unpack command (V4) first, which writes the arrays back and clears the journal entry.

### Read models: shared vectors (opt-in)

- **Opt-in.** `[VectorField(3072, Shared = true)]` (working name). Nothing changes for a field without it.
- **Storage.** Per shared field, a vector table beside the perspective table (working name `<table>__<column>_vectors`):
  `vector_hash bytea PRIMARY KEY` (SHA-256 of the binary32 bits, computed in C#, mirrored by a SQL function pinned to
  the C# result by a test), `vector vector(N) NOT NULL`. The perspective row holds `<column>_hash bytea` with a foreign
  key to it (`ON DELETE RESTRICT`) and an index. A `[VectorField]` index (`[Indexed]`) is built on the vector table.
- **Write.** In the row's transaction: insert the vector (`ON CONFLICT DO NOTHING`), take `FOR KEY SHARE` on it, then
  write the row with the hash. The key-share lock is what stops the sweep from deleting a vector between the insert
  and the row write.
- **Read.** Split-mode hydration (`PhysicalFieldHydratorRegistry`) and the search extensions
  (`src/Whizbang.Data.EFCore.Postgres/VectorSearchExtensions.cs`: `OrderByCosineDistance`, `WithinCosineDistance`,
  `WithinL2Distance`, `WhereHasVector`, which read the vector as a shadow property today) join the vector table. The
  Dapper store does the same join in SQL.
- **Reclaim.** A maintenance sweep deletes vectors no row references, in batches, with `SKIP LOCKED`.
- **Converting a deployed field.** Turning `Shared` on or off for a field with rows is a perspective stored-form
  conversion (`wh_stored_form_migrations`), journaled with its versions so the rollback gate sees it.

## Code it touches

| Area | Files and types |
|---|---|
| Vector field attribute | `src/Whizbang.Core/Perspectives/VectorFieldAttribute.cs` (`Shared`) |
| Generators, read models | `src/Whizbang.Generators/PerspectiveSchemaGenerator.cs` (the `vector(N)` column at line 822 today), `src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs` (`_extractVectorFieldInfo`, the column type at line 753), `EFCoreServiceRegistrationGenerator.cs` (line 1495), `Templates/EFCoreConfigurationTemplate.cs`, `src/Whizbang.Generators.Shared/Models/PhysicalFieldInfo.cs`, `PhysicalColumnSql.cs`, `Utilities/SchemaHashUtilities.cs`, `Schemas/perspective-schema.json`; analyzers `VectorDependencyAnalyzer.cs`, `Analyzers/PerspectiveFieldAnalyzer.cs`, `VectorFieldPackageReferenceAnalyzer.cs` |
| Read-model runtime | `src/Whizbang.Core/Perspectives/PerspectivePhysicalFieldRegistry.cs`, `SplitPhysicalFieldMap.cs`; `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs`, `PhysicalFieldHydratorRegistry.cs`, `QueryTranslation/PhysicalFieldRegistry.cs`, `QueryTranslation/PhysicalFieldExpressionVisitor.cs`, `VectorSearchExtensions.cs`; `src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs`; collectives that set a vector (`src/Whizbang.Data.Postgres/Collective/CollectivePhysicalColumns.cs`, `DapperCollectiveSpecCompiler.cs`, `EFCoreCollectiveAdapter.cs`): a collective `SetProperty` on a shared vector is refused by an analyzer in V6 unless it is supported there |
| Rebuild into a new table | `src/Whizbang.Data.Postgres/Perspectives/PostgresPerspectiveTableSwapper.cs`: the vector table outlives the swap, and the foreign key follows the swapped table |
| Event bodies, C# | `src/Whizbang.Generators/MessageJsonContextGenerator.cs`, `JsonMessageTypeInfo.cs` (the converter and the path registry), a new opt-out attribute in `src/Whizbang.Core/Attributes/`, the startup step that writes `wh_event_vector_paths` |
| Event bodies, SQL | the two emit chains and `wh_append_event` (redefined whole in a new migration, README rule 5), a new `_wh_pack_vectors(jsonb, text[][])`, the switch and reader marker |
| Gate | `src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs`, `src/Whizbang.Data.Postgres/MigrationReversal.cs`, `src/Whizbang.Core/Versioning/UpgradeOrder.cs` (the irreversible conversion), the instance registration that writes the marker |

## Migrations

Every SQL change is a new migration, numbered above develop (626 today) and every open pull request when it is written.
Expected: one for V3 (paths table, packing function, redefined emit chains and `wh_append_event`, switch, reader marker
column in the bootstrap region), one for V4 (the backfill and unpack functions and their journal rows), one for V7 (the
sweep function). V6's tables and columns are application schema changes the generator emits. Hot-path SQL is measured on
a copied table first, and linted with `scripts/Lint-MigrationSql.ps1`.

## Slices

Each slice is one issue, red before green (`ai-docs/tdd-strict.md`), 100% line and branch coverage of new code, no
reflection, both drivers where it applies, fixtures serialized from real types, and its docs in the same change.

### V1. Readers accept packed vectors; the generator knows every vector property

- **Changes.** The converter (reads array or packed string, writes array), attached by the message JSON context generator
  to every vector property of every event type; the opt-out attribute; the per-type path registry emitted beside it.
  No write packs yet.
- **RED first.** Round trip of the packed form is bit-exact for random vectors and for `-0`, the smallest subnormal,
  `float.MaxValue` and `float.Epsilon`; both forms read to equal arrays; a string that is not valid base64 of a multiple
  of four bytes fails with a `JsonException` naming the property; `double[]` gets no converter and no path; an
  opted-out property gets neither; a nested object's vector gets the dotted path; an array of objects with vectors gets
  no path and an info diagnostic; each emitted path, applied to a real serialized instance of its type, finds an array
  (so a naming-policy or `[JsonPropertyName]` mismatch fails the build's tests, never packs nothing silently).
- **Docs.** `data/event-store.md` (stored form versus wire form; packed vectors); the diagnostics page for the info id.

### V2. Packing at the stored body, behind the gate

- **Changes.** New migration: `wh_event_vector_paths`, `_wh_pack_vectors`, the emit chains and `wh_append_event`
  redefined to pack the body under the switch, the reader marker column, the switch and the maintenance tick that opens
  it, and the first-pack journal entry. The startup step writes the paths.
- **RED first (SQL, real Postgres).** A packed body's bits equal the C# `float` bits for the same event (the SQL side
  against the C# side); a vector event stored on the outbox path, the inbox path and the direct append is packed when
  the switch is on and not packed when it is off; the outbox row and the transport message keep JSON floats; an
  ordinary event takes the same statements as before (the fixed-cost tests stay green, and a new one names packing);
  the switch stays off while one live instance lacks the marker and opens when it leaves or gains it; the first pack
  writes the journal entry once; a later rollback stands down at Assess with a reason naming packed vectors
  (`StartupAssessorTests` style).
- **Docs.** `operations/startup/rolling-upgrades.md` (the gate), `operations/configuration/configuration-reference.md`
  (the switch, if exposed as an option), `data/event-store.md`.

### V3. Exports send the wire form

- **Changes.** Every path that sends a stored body to another service re-serializes through C#. Inventory derived by
  machine (every reader of `wh_event_body.event_data` that leaves the process) and checked against the transport's
  publish seam.
- **RED first.** An integrity repair copy of a packed event, received by a reader without the converter (a plain
  `JsonSerializer` over the payload type), reads the array.
- **Docs.** `data/event-store.md` (what leaves the database).

### V4. History: pack what is stored, and an unpack command

- **Changes.** A resumable maintenance pass that packs existing vector bodies in batches once the switch is on, journaled
  as a stored-form conversion; an operator command that unpacks everything for a planned downgrade and clears the
  journal entry. The space returns through the table rewrite seam (WS-4).
- **RED first.** A pass interrupted after one batch resumes after it and packs each body once; the pass does nothing
  while the switch is off; unpack restores bodies byte-equal to their pre-pack JSON array values; after unpack, an older
  build's Assess no longer stands down for packed vectors.
- **Docs.** `operations/infrastructure/migrations` (stored-form conversions), the maintenance page, the storage program.

### V5. Measure event bodies before and after

- **Changes.** None in the library. Re-run the read-only bytes-per-event-type query and the WS-6 meters
  ([storage-telemetry.md](storage-telemetry.md)) on a representative database before V2 and after V4, recording
  `pg_column_size` per vector event and the `wh_event_body` size. Decide on the raw `bytea` form only from these numbers.
- **Acceptance.** The numbers are recorded in this plan and on the issue.
- **Measured (2026-11-28, `PackedVectorBodySizeScenarioTests`, local PostgreSQL, 200 events of 3,072 floats, per
  event, `pg_column_size` after the body column's lz4).** The saving depends on the vectors' digits, not their size:

  | Corpus | JSON numbers, text / stored | Packed, text / stored | Stored change | Table after a rewrite |
  |---|---|---|---|---|
  | Full precision (shortest round-trip text, as a model writes it) | 41,218 / 26,613 B | 16,472 / 16,476 B | 38.1% smaller | 35.4% smaller |
  | Rounded to 4 decimals | 25,850 / 11,351 B | 16,472 / 12,960 B | 14.2% larger | 16.1% larger |

  The raw bits alone store in 12,292 B. Why: jsonb holds each number as a `numeric`, which lz4 compresses in proportion
  to how few digits it has, while the binary32 bits of a decimal value are close to random, so base64 of them barely
  compresses. The earlier consumer measurement (about 18.6 KB to 16.4 KB, about 17%) sits between the two corpora: its
  vectors carry fewer digits than full precision. A raw `bytea` form would reach 12.3 KB in both cases (a further 25%
  over packed at full precision), and with 4 decimals it is still larger than the JSON numbers.
- **Owner decision (2026-11-28).** Packing stays on by default. The history pass (V4) packs a body only when the
  packed body stores smaller, judged as storage pays for it: each candidate is written to a transaction-local copy of
  `wh_event_body` with the same compression and its `pg_column_size` is compared with the stored body's. A body kept as
  numbers is counted as kept, is not recorded in `wh_packed_vector_paths`, and is not read again once the walk
  completes. The write paths are unchanged and pack every vector body once the gate is open; a vector rounded before it
  is written is marked `[StoredAsJsonArray]` to keep new bodies as numbers. The unpack command stays SQL only, and its
  hold ends only on an explicit `wh_resume_vector_packing`. A raw `bytea` form waits on a read-only measurement of the
  digits real vectors carry.

### V6. Shared vectors for an opted-in field

- **Changes.** `VectorFieldAttribute.Shared`; the generator emits the vector table, the hash column, its foreign key and
  index, and moves a declared vector index to the vector table; both stores write through insert-or-reference with the
  key-share lock; hydration and the search extensions join; the table swapper keeps the vector table.
- **RED first (both drivers, real Postgres).** Two rows with the same vector store one vector row; a row read back has
  the exact vector; cosine, L2 and inner-product search over a shared field return the same rows in the same order as
  over an unshared copy of the data; a field without `Shared` generates exactly today's schema (snapshot); a collective
  that sets a shared vector is refused by an analyzer; a rebuild through the table swapper keeps every reference valid.
- **Docs.** `fundamentals/perspectives/vector-fields.md` (shared storage: when to choose it, what it costs),
  `fundamentals/lenses/vector-search.md`, `extending/features/vector-search.md`, `data/perspectives-storage.md`.

### V7. Reclaim unreferenced shared vectors

- **Changes.** New migration: a sweep function the maintenance tick runs per vector table, batched, `SKIP LOCKED`.
- **RED first.** A vector no row references is deleted; a vector a row references is kept; a writer that holds
  `FOR KEY SHARE` on a vector while the sweep runs keeps it (held at a named point, no delay); a sweep interrupted after
  one batch resumes.
- **Docs.** `fundamentals/perspectives/vector-fields.md` (reclaim), the maintenance page.

### V8. Turning `Shared` on or off for a deployed field

- **Changes.** A generated stored-form conversion that moves inline vectors into the vector table (or back), batched and
  journaled, with reads that accept either location until it finishes, after which the old column is dropped by a
  generated schema change.
- **RED first.** Rows written inline before the switch read the same vector during and after the conversion; a
  conversion interrupted after one batch resumes; the journal records the versions so a rollback across it stands down;
  turning `Shared` off restores the inline column with the exact vectors.
- **Docs.** `fundamentals/perspectives/vector-fields.md`, `operations/infrastructure/migrations`.

## Docs pages (docs repo, `src/assets/docs/v1.0.0/`)

- `data/event-store.md`: stored versus wire form, packed vectors, exports (V1, V2, V3)
- `operations/startup/rolling-upgrades.md`: the packing gate (V2)
- `operations/infrastructure/migrations`: stored-form conversions for packing and shared vectors (V4, V8)
- `operations/configuration/configuration-reference.md`: any option added (V2, V6)
- `fundamentals/perspectives/vector-fields.md`, `fundamentals/lenses/vector-search.md`,
  `extending/features/vector-search.md`, `data/perspectives-storage.md`: shared vectors (V6 to V8)
- `operations/diagnostics/whizNNN.md` per new id, and `error-reference.md` (V1, V6)
- A design-choices entry per decision (no half precision; shared opt-in; packing default-on with the gate), each with a
  test that fails if it flips: a `[VectorField]` without `Shared` stays inline; a new event type's `float[]` is packed
  unless opted out; `double[]` is never packed

## Open risks

- **Packing cost on the write path.** Converting 3,072 numbers per event in SQL is real CPU during a bulk import. V2
  measures it on a copied table before the migration is final; the fallback is to pack in C# and pass the stored payload
  beside the wire payload, which widens the store call.
- **A SQL reader of a vector field.** Nothing in SQL is known to read a vector out of a body (superseding keys,
  collective predecessor fields and scope do not). V2 adds a lint over migrations for a JSON path that names a
  registered vector path.
- **Content-hash bodies (#2438).** Hashing must use the stored form consistently; packed and unpacked copies of one body
  hash differently. #2438's design should hash after packing.
- **Several services in one schema.** Paths are written per service for the types it knows. Packing is a storage choice
  and every reader in the schema reads both forms behind the gate, so a type packed by one service's registration and
  not another's is still readable; V2 states which registration wins and tests it.
- **Search plans with shared vectors.** An approximate index on the vector table plus a join may not be used for
  `ORDER BY ... LIMIT` the way an index on the row table is. V6 compares plans for indexed fields and documents the result.
- **Backfill rewrites history's bytes.** V4 changes stored bodies without changing their values or ids (integrity
  digests fold event ids, not bodies). If the owner prefers history to stay as written, V4 becomes opt-in; it is
  default-on here because it follows the default-on packing decision.
