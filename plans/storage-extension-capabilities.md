# Postgres extension capabilities (WS-8)

One startup answer to "which Postgres extensions can Whizbang use here, and what do they give me?",
plus a hard stop when an operator explicitly asked for a feature the server cannot provide. The
bloat work (`storage-bloat.md`, online rewrite through pg_squeeze) and the ledger-retention work
(the optional pg_partman setting) both depend on it, so it lands first.

Defect 8 (a removed configuration key is ignored without a word) is folded in as the last slice,
because it reports through the same startup surface.

## Context

Evidence from a production deployment's development environment, on managed Postgres 17:

- pg_repack, pg_squeeze and pg_partman are installed on the server but **not allowlisted**, so
  `CREATE EXTENSION` refuses them. vector, pg_trgm, pg_stat_statements and pg_cron are allowlisted.
- Nothing in the framework reported any of this. An operator learns an extension is unusable only
  when a feature quietly degrades or a raw `PostgresException` surfaces.
- `Whizbang:WorkCoordinator:MaintenanceInterval` was removed with the legacy publisher worker
  (commit `6e152a56d`). Deployed configuration still sets it, and nothing says it is ignored.

What the code does today:

| Extension | Where | Behavior |
|---|---|---|
| pg_trgm | `src/Whizbang.Data.Postgres/OptionalExtensionBlocks.cs:38-65` (refusal set), `:80` (`IsUnavailable`), `:152` (`ApplyAsync`), `:202` (`CREATE EXTENSION`) | Blocks marked as optional are skipped with a warning when the server refuses with `0A000` (not allowlisted), `42501` (no privilege) or `58P01` (files missing). Emitted by `src/Whizbang.Generators.Shared/Models/JsonIndexInfo.cs:372,432` and `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:3236` |
| vector | `EFCoreServiceRegistrationGenerator.cs:2242-2260` and `:2393-2411` (data source build), `:2812-2820` (schema script `DO` block) | Hard-coded check-then-`CREATE EXTENSION vector`. A refusal is a raw exception with no explanation |
| pg_stat_statements | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs:206` | Read by `GetExpensivePredicatesAsync`; any `PostgresException` is caught and the answer is empty |
| pgstattuple | `PostgresTableStatisticsProvider.cs:26-44` | Deliberately avoided; bloat is estimated from `pg_stats` |
| pg_cron | `src/Whizbang.Data.Postgres/Migrations/067_TemporalCronNextFire.sql:3` | Deliberately not used; recurrence is home-grown |

There is no catalog, no startup report and no fail-fast path.

## Goal

1. Every extension Whizbang can use is probed once per database at startup and given one state.
2. One informational log block lists the usable extensions and what each enables.
3. A feature the operator **explicitly** configured, whose extension is not usable, stops startup
   with a message naming the feature, the setting key, the extension's state and the fix.
4. A feature left on its default degrades with a warning, as pg_trgm does today.
5. Requirements are declared as metadata on configuration entries, read without reflection, so a
   third-party package can declare its own the same way.
6. Unknown `Whizbang:*` configuration keys produce a startup warning.

## Design

### Catalog

A static, built-in list in `Whizbang.Data.Postgres` (new `PostgresExtensionCatalog.cs`).

| Extension | Enables | Whizbang may create it | Extra prerequisites |
|---|---|---|---|
| vector | vector fields and their indexes | yes | none |
| pg_trgm | substring (trigram) indexes | yes | none |
| pg_squeeze | online table rewrite (`storage-bloat.md`, C1) | yes | `shared_preload_libraries`, logical `wal_level` |
| pg_partman | optional partition manager; Whizbang has a built-in fallback | yes | none (its background worker is optional) |
| pg_repack | detection and runbook only | no | a client binary run outside the process |
| pgstattuple | exact bloat measurement, otherwise estimated | yes | none |
| pg_stat_statements | diagnostics, expensive-predicate advice | yes | `shared_preload_libraries` |
| pg_cron | optional in-database scheduling | no (lives in one database) | `shared_preload_libraries` |

### Probe and states

One query per database: extensions are per database, while the allowlist and preload list are per
server. It reads `pg_extension`, `pg_available_extensions`, `current_setting('azure.extensions', true)`
(NULL when the setting does not exist) and `current_setting('shared_preload_libraries')`.

| State | Meaning |
|---|---|
| Installed | present in `pg_extension`, and preloaded when the catalog says it must be |
| Available | creatable: control files present, allowlisted (or no allowlist exists), preloaded if needed |
| NotAllowlisted | control files present, but an allowlist exists and does not name it |
| NeedsPreload | allowlisted or installed, but its library is not in `shared_preload_libraries` |
| Missing | no control files on the server |
| Unknown | the probe could not run (database unreachable, query refused) |

The allowlist comparison is case-insensitive and trims whitespace (managed servers list names in
upper case). The result is held in an `IPostgresCapabilities` singleton keyed by database, with a
`Refresh` method the maintenance cycle may call.

### Startup report and gauge

- One Information block per database: each usable extension and what it enables. Extensions that
  are relevant but unusable are listed at Debug, except when a feature asked for them (then the
  validation below decides).
- `whizbang.postgres.extension.state`, an observable gauge with value 1 and tags `extension`,
  `database`, `state`, on a new meter `Whizbang.Postgres`. It feeds `storage-telemetry.md`.

### Requirement metadata (AOT-safe)

- **Attribute.** `[RequiresPostgresExtension("pg_squeeze", Feature = "Online table rewrite")]` in
  `Whizbang.Core.Configuration` (beside `WhizbangOptions`), so options classes in `Whizbang.Core`
  can carry it without referencing a Postgres package. Valid on properties, enum members and
  attribute classes (for `[VectorField]` and trigram `[Indexed]`).
- **Read by a source generator, never by reflection.** A new `ConfigurationManifestGenerator` in
  `src/Whizbang.Generators/`, modeled on `ServiceRequirementsGenerator.cs` (syntax filter at `:87`,
  manifest emitted per assembly at `:286-316`). It recognizes each
  `ConfigurationBinder.Bind(configuration.GetSection("<literal>"), options)` call (the shape every
  framework binding uses, for example `src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs:789`),
  walks the bound type's settable properties at compile time, and emits:
  - the full key tree under that section (used by slice 8);
  - one requirement record per attributed property or enum member: section path, property, the
    triggering enum value if any, extension name, feature text.
- **Registration.** The emitted class registers itself through `[ModuleInitializer]` into a
  `ConfigurationManifestRegistry` built on `src/Whizbang.Core/Registry/AssemblyRegistry.cs`, the
  existing multi-assembly pattern. A third party referencing the Whizbang packages gets the same
  generator in its compilation, so attributing its own options is all it does.
- **Schema attributes.** `EFCoreServiceRegistrationGenerator` already knows when vector fields or
  trigram indexes exist (`hasVectorFields`, `JsonIndexInfo`). It emits an
  `IExtensionRequirementProvider` registration listing them, so they enter the same check.
- **Runtime seam.** `IExtensionRequirementProvider` (DI, enumerable) returns requirements that are
  only known at run time. Third parties can implement it directly.

### Validation rule

| Setting | Extension usable | Outcome |
|---|---|---|
| explicit | yes | proceed |
| explicit | no (known state) | **throw at startup** |
| explicit | Unknown | warn; the feature fails loudly when it runs |
| default or `Auto` | yes | use it |
| default or `Auto` | no | degrade with a warning, as pg_trgm does today |

- **Explicit** means the configuration key exists (`IConfiguration.GetSection(key).Exists()`) or the
  value differs from the declared default. Every extension-backed setting defaults to `Auto` (or
  `false`), so "set in code" is caught by the value comparison without tracking call sites.
- **Mechanism.** Validators are `IValidateOptions<T>`, so they compose with a host's own options
  validation. They are run by a new startup `IHostedService` after the async probe, following
  `src/Whizbang.Core/DependencyInjection/RegistrationValidationStartup.cs:36-50` (an
  `IHostedService` whose `StartAsync` throws to fail host start). Not inside the startup pipeline:
  `src/Whizbang.Core/Startup/StartupPipelineHosting.cs:87` states nothing inside it may stop the host.
  Not `ValidateOnStart` alone: it runs before any async work, and `IOptions<T>` caches the first
  result, so a pre-probe pass would be cached as success.
- **Message template:** `Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy =
  PgSqueeze) needs the pg_squeeze extension, which is NotAllowlisted on database '<name>'. Fix: add
  PG_SQUEEZE to azure.extensions and to shared_preload_libraries, then restart the server.` The fix
  text comes from the catalog entry and the state.

### Unknown configuration keys (defect 8)

The configuration binder generator already emits a key set per bound type and honors
`BinderOptions.ErrorOnUnknownConfiguration`. It cannot be used as is: it throws rather than warns,
and the root `Whizbang` section is bound to `WhizbangOptions`
(`WorkerPipelineExtensions.cs:915-925`), whose children are mostly other sections, so every
section would read as unknown. The manifest above holds the whole tree instead. At startup, every
leaf path under `Whizbang` (environment variables arrive with `:` already) that no manifest entry
covers is logged as a warning, once. Dictionary-typed properties are wildcards (for example
`Whizbang:PerspectiveRowRetention:Overrides:<Model>`). Sections read by hand, such as
`Whizbang:DeadLetterRecovery` (`WorkerPipelineExtensions.cs:936`), are declared through an explicit
manifest entry. An assembly-level `[RemovedConfigurationKey]` carries removed keys with a
replacement hint, so the warning for `MaintenanceInterval` can name
`Whizbang:Workers:Maintenance:IntervalMinutes`.

## Configuration keys

`Whizbang:Postgres` is not used: its children are database names
(`src/Whizbang.Data.Postgres/PostgresOptionsPostConfigure.cs:33-36`).

| Key | Environment variable | Default | Meaning |
|---|---|---|---|
| `Whizbang:PostgresExtensions:Policies:<extension>` | `Whizbang__PostgresExtensions__Policies__pg_trgm` | `Auto` | `Auto`, `Required` (explicit: throw if unusable) or `Disabled` (never create or use) |
| `Whizbang:PostgresExtensions:ProbeOnStartup` | `Whizbang__PostgresExtensions__ProbeOnStartup` | `true` | `false` skips the probe; every state is Unknown |
| `Whizbang:Configuration:UnknownKeys` | `Whizbang__Configuration__UnknownKeys` | `Warn` | `Warn`, `Throw` or `Ignore` |

## Slices

Each slice is one red-then-green increment and its own commit.

1. **Catalog, probe, states.** New `PostgresExtensionCatalog.cs`, `PostgresCapabilityProbe.cs`,
   `IPostgresCapabilities` in `src/Whizbang.Data.Postgres/`.
   RED: state derivation as a pure function over a probe row, one test per state, plus allowlist
   parsing (upper case, spaces, empty, absent setting means no allowlist). Integration test on the
   shared container (`src/Whizbang.Testing/Containers/SharedPostgresContainer.cs:254` preloads only
   pg_stat_statements): vector Available, pg_stat_statements Installed after create, pg_squeeze
   Missing, nothing NotAllowlisted. Unreachable database gives Unknown, never a throw.
   Docs: new `operations/infrastructure/postgres-extensions.md`.
2. **Startup report and gauge.** RED: the log block lists exactly the usable extensions with their
   purpose; the gauge reports one series per extension and database. Docs: the new page, and
   `operations/observability/metrics.md` (new `Whizbang.Postgres` section).
3. **Attribute, manifest generator, registry.** RED: generator tests beside
   `tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs`: an attributed property, an
   attributed enum member, a nested section, a dictionary wildcard, a private type (skipped), a
   second assembly registering through the module initializer. Docs: new page, "Declaring a
   requirement" section; `operations/configuration/configuration-reference.md` (policy keys).
4. **Validation and fail-fast.** RED, in the component test project: explicit plus NotAllowlisted
   fails host start with the full message; explicit plus Unknown warns and starts; `Auto` plus
   Missing warns and starts; `Disabled` never probes the create path. Docs: new page, "When startup
   stops".
5. **pg_trgm onto the catalog.** `OptionalExtensionBlocks.ApplyAsync` consults the capability
   before trying `CREATE EXTENSION`; the refusal classification stays as the fallback. A
   `Required` policy turns today's skip into the startup error. RED: extend
   `tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/OptionalExtensionBlocksTests.cs`. Docs:
   `fundamentals/perspectives/perspective-indexes.md`.
6. **pgvector onto the catalog.** The three generated sites call one runtime helper instead of
   inline SQL. Vector fields are required by construction (the column type cannot exist without the
   extension), so a refusal becomes the catalog's explanatory error. RED: generator snapshot tests
   for the emitted call; integration test for the message. Docs:
   `fundamentals/perspectives/vector-fields.md`.
7. **Existing readers.** `GetExpensivePredicatesAsync` checks the capability instead of catching
   every `PostgresException`. RED: Installed reads; NeedsPreload returns empty without a query.
8. **Unknown keys (defect 8).** Startup warning from the manifest, `[RemovedConfigurationKey]` for
   `Whizbang:WorkCoordinator:MaintenanceInterval`. RED: a removed key warns with its replacement;
   a typo under a known section warns; a dictionary entry does not; `Throw` fails start. Docs:
   `operations/configuration/configuration-reference.md`, section "Every key on this page is one the
   library reads".

## Engineering standards

- Strict red before green (`ai-docs/tdd-strict.md`); every slice starts with a failing test.
- 100% of new lines and hand-written branches (`ai-docs/coverage-exclusions.md`).
- No reflection in library code (`ai-docs/aot-requirements.md`): attribute metadata is read only by
  the generator; runtime reads generated arrays.
- Generator rules: sealed value-type records, syntactic filter first
  (`src/Whizbang.Generators/ai-docs/performance-principles.md`).
- Public types carry `<docs>` and `<tests>` tags (`ai-docs/documentation-maintenance.md`).
- No migration is expected. If one is needed, it follows
  `src/Whizbang.Data.Postgres/Migrations/README.md` (schema-qualify, idempotent, constants from
  `constants.txt`, `<docs>`/`<tests>` on every function).
- `Lint-MigrationSql.ps1` and `dotnet format` before the PR.

## Verification

- Against the managed development server: the report shows vector, pg_trgm, pg_stat_statements
  and pg_cron as usable and the three non-allowlisted extensions as NotAllowlisted.
- Setting `TableRewriteStrategy=PgSqueeze` there stops startup with the fix text; removing it
  starts normally.
- A deployment still carrying `MaintenanceInterval` logs one warning naming the replacement.

## Open questions

- Add a seventh state, NoPrivilege (allowlisted but this role cannot create it; `42501` today)?
  The probe can tell from `pg_available_extension_versions.trusted` and the database `CREATE`
  privilege.
- Should Unknown plus explicit throw after a bounded retry, rather than warn?
- `Whizbang.Postgres` as a new meter, or the existing `Whizbang.TableStatistics`?
- Does `Throw` for unknown keys belong in 1.0, or only `Warn`?
