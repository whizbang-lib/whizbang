# External capabilities, Postgres extensions first (WS-8)

One repeatable pattern for everything Whizbang depends on outside its own process: detect it,
provision and manage it where allowed, verify it, and report it the same way every time. Postgres
extensions are the first provider (issue #1498); the pattern itself is issue #1513, and it is built
so other database engines, broker plugins and third-party packages follow it unchanged. The
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

## Responsibilities

| Who | Does |
|---|---|
| Operator (server level) | Allows the extension on the server: the managed-server allowlist, `shared_preload_libraries` and the restart it needs, the pg_repack client program where that run mode is used. Whizbang cannot do these. |
| Whizbang (database level) | Everything after that: detect it, `CREATE EXTENSION`, `ALTER EXTENSION ... UPDATE`, run what the extension offers from its own workers, and report the result. |

Whizbang still detects and uses an extension an operator created by hand, including when
provisioning is switched off.

## Goal

1. Every capability Whizbang can use is probed once per scope at startup (for Postgres, per
   database) and given one generic state.
2. Required capabilities are provisioned and kept current by one runner, unless the operator
   switched provisioning off globally, per provider or per capability.
3. Startup prints **one verdict line per required capability**: enabled (created, already present
   or upgraded, with the version) or the issue and its fix, naming **every feature that requires it
   and the setting that turned the feature on**.
4. A feature the operator **explicitly** configured, whose capability is not usable, stops startup.
   A feature on by default degrades with a warning, as pg_trgm does today.
5. Requirements are declared as metadata on configuration entries and schema attributes, read
   without reflection, so a third-party package declares its own the same way.
6. Unknown `Whizbang:*` configuration keys produce a startup warning (defect 8).

## Design: the capability pattern (#1513)

Every external dependency is a **capability** identified by `(provider, name)`, for example
`postgres/pg_squeeze`. The same seven pieces apply to every provider; core
(`src/Whizbang.Core/Capabilities/`) holds the generic parts and never references a provider package.

| # | Piece | Generic (core) | Postgres provider |
|---|---|---|---|
| 1 | Descriptor | `CapabilityDescriptor`: purpose, features, provisionable, prerequisites, fix per state | `PostgresExtensionCatalog` |
| 2 | Probe | `ICapabilityProvider.ProbeAsync(scope)`, `ICapabilityRegistry` aggregates providers; a throwing probe reads Unknown | one catalog query per database |
| 3 | Requirements | `[RequiresCapability(provider, name, Feature = ...)]`, collected by a source generator into a registry via module initializer; runtime seam `ICapabilityRequirementSource` | shorthand `[RequiresPostgresExtension("pg_squeeze")]`; vector columns and trigram indexes found in the schema |
| 4 | Activation | explicit (key set, or value differs from the declared default) vs implicit (a default turned the feature on) | n/a |
| 5 | Provisioning | one runner: Ready with newer version → upgrade; Provisionable → provision; re-probe; record the outcome | `CREATE EXTENSION` / `ALTER EXTENSION UPDATE` under the schema lock |
| 6 | Verdict | one line per required capability, same format for every provider; explicit issue fails start from a hosted service after the probe | fix text names the allowlist, preload and restart |
| 7 | Telemetry and health | gauge `whizbang.capability.state` on meter `Whizbang.Capabilities` (tags `provider`, `capability`, `scope`, `state`, `required`); health entry per provider | scope = database |

### Generic states

| State | Meaning | Postgres detail it maps from |
|---|---|---|
| Ready | installed or enabled and usable | Installed (and preloaded where needed) |
| Provisionable | absent, and Whizbang may provision it | Available |
| NotPermitted | the platform blocks it | NotAllowlisted |
| NeedsServerChange | needs operator action at the server | NeedsPreload |
| NoPrivilege | the identity Whizbang uses cannot provision it | NoPrivilege |
| Missing | not on this server at all | Missing |
| Unknown | could not be determined (probe failed or disabled) | Unknown |

The provider's own state is kept as the detail, so the log and gauge never lose it.

### Verdict format

```
capability postgres/pg_squeeze (db orders): Ready, created 1.7.0. Required by: Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)
capability postgres/pg_partman (db orders): NotPermitted. Fix: add PG_PARTMAN to azure.extensions. Required by: Partition management (Whizbang:Workers:Maintenance:Partitioning:Strategy = Auto, default); using the built-in manager
```

Usable capabilities that nothing requires are listed once at Information; unusable ones nothing
requires go to Debug.

### Validation rule

| Activation | Capability usable | Outcome |
|---|---|---|
| explicit | yes | proceed |
| explicit | no (known state) | **throw at startup** with the verdict line |
| explicit | Unknown | warn; the feature fails loudly when it runs |
| implicit | yes | use it |
| implicit | no | degrade with a warning |

The check runs in an `IHostedService` after the probe, following
`src/Whizbang.Core/DependencyInjection/RegistrationValidationStartup.cs:36-50`. It cannot run inside
the startup pipeline (`src/Whizbang.Core/Startup/StartupPipelineHosting.cs:87`: nothing inside it may
stop the host) or through `ValidateOnStart` alone (it runs before the async probe, and `IOptions<T>`
caches the first result).

## Postgres extensions: how each is managed

| Extension | Enables | Whizbang provisions | How Whizbang runs it | Operator prerequisite |
|---|---|---|---|---|
| vector | vector fields and their indexes | yes | column types and indexes from the schema | allowlist |
| pg_trgm | substring (trigram) indexes | yes | index DDL from the schema | allowlist |
| pg_squeeze | online table rewrite (`storage-bloat.md`) | yes | the table rewriter calls `squeeze.squeeze_table(schema, table)` from the maintenance worker | allowlist, `shared_preload_libraries`, logical `wal_level`, restart |
| pg_partman | partition management (`storage-ledger-retention.md`) | yes | `partman.create_parent` when a ledger is converted; `partman.run_maintenance` from the maintenance worker each cycle | allowlist (its background worker is not used) |
| pg_repack | table rewrite without a long lock | yes (the extension) | either the `pg_repack` client run **in process** by the maintenance duty holder, or an **external job** that runs the same work from the queue | allowlist; the client program on `PATH` or at a configured path (in-process mode) |
| pgstattuple | exact bloat measurement | yes | read by table statistics when Ready; otherwise estimated | allowlist |
| pg_stat_statements | expensive-predicate advice | yes | read by the index advisor when Ready | `shared_preload_libraries` |
| pg_cron | not used | no | detection only | n/a |

**pg_repack run modes.** `InProcess`: the instance holding the maintainer duty runs the client
against the queued tables inside the idle window, with the connection's credentials passed through
the environment (never on the command line). `External`: Whizbang only queues and reports; a
separate job (a container or scheduled task built from the same package's command-line helper)
claims the queue and runs the client. Both read the same `pending_table_rewrites` queue and clear
a request only after the re-measure improves.

## Configuration keys

| Key | Environment variable | Default | Meaning |
|---|---|---|---|
| `Whizbang:Capabilities:ProbeOnStartup` | `Whizbang__Capabilities__ProbeOnStartup` | `true` | `false` skips the probe; every state reads Unknown |
| `Whizbang:Capabilities:AutoProvision` | `Whizbang__Capabilities__AutoProvision` | `true` | global switch for create and upgrade |
| `Whizbang:Capabilities:<provider>:AutoProvision` | `Whizbang__Capabilities__postgres__AutoProvision` | inherits | per provider |
| `Whizbang:Capabilities:<provider>:<name>:AutoProvision` | `Whizbang__Capabilities__postgres__pg_squeeze__AutoProvision` | inherits | per capability |
| `Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode` | `Whizbang__Workers__Maintenance__TableRewrite__PgRepack__Mode` | `InProcess` | `InProcess` or `External` |
| `Whizbang:Workers:Maintenance:TableRewrite:PgRepack:ClientPath` | `Whizbang__Workers__Maintenance__TableRewrite__PgRepack__ClientPath` | `pg_repack` (from `PATH`) | client program for `InProcess` |
| `Whizbang:Configuration:UnknownKeys` | `Whizbang__Configuration__UnknownKeys` | `Warn` | `Warn`, `Throw` or `Ignore` |

Feature keys that activate a capability live with their feature:
`Whizbang:Workers:Maintenance:TableRewriteStrategy` (`storage-bloat.md`) and
`Whizbang:Workers:Maintenance:Partitioning:Strategy` (`storage-ledger-retention.md`).
`Whizbang:Postgres` is not used for any of this: its children are database names
(`src/Whizbang.Data.Postgres/PostgresOptionsPostConfigure.cs:33-36`).

## Slices

Each slice is one red-then-green increment and its own commit.

1. **Pattern core, Postgres provider, probe, report, gauge.** DONE in PR #1510 (issue #1511):
   `src/Whizbang.Core/Capabilities/` (id, descriptor, generic states, provider and registry,
   `Capabilities` startup step after `Migrate`, `Whizbang.Capabilities` meter), the Postgres catalog
   and probe in `src/Whizbang.Data.Postgres/`, `Whizbang:Capabilities:ProbeOnStartup`. Docs:
   `operations/infrastructure/capabilities.md` and the Postgres provider page.
2. **Requirement attribute, manifest generator, registry.** DONE on branch `feat/1703-ws8s2` (issue #1703):
   the two attributes in `src/Whizbang.Core/Capabilities/`, `CapabilityRequirement`, the
   `ConfigurationManifestGenerator` (template `ConfigurationManifestTemplate.cs`), and
   `ConfigurationManifestRegistry` / `ConfigurationManifest` in `src/Whizbang.Core/Configuration/`. Recognized
   bindings: `Bind` (both forms), `Get<T>`, `services.Configure<T>(section)`, `OptionsBuilder<T>.Bind(section)`,
   over `GetSection` chains with constant keys. Whizbang.Core's generated key tree matches the recorded
   manifest exactly for every section it binds literally (283 keys). Left for slice 3: the
   `ICapabilityRequirementSource` seam (the registry is static and module-initialized; the verdict should read
   requirements through the seam so slice 6's vector columns can add a runtime source), and each setting's
   declared default (needed for "a default turned the feature on"). Not replaced: the recorded key manifest
   and `RemovedConfigurationKeys` (no `[RemovedConfigurationKey]` yet). Original scope: `[RequiresCapability]` and
   `[RequiresPostgresExtension]` in core; a `ConfigurationManifestGenerator` in
   `src/Whizbang.Generators/`, modeled on `ServiceRequirementsGenerator.cs`, recognizing each literal
   `GetSection("<key>")` binding and emitting the key tree plus one requirement per attributed
   property or enum member, registered through `[ModuleInitializer]`. RED: generator tests for an
   attributed property, an enum member, a nested section, a dictionary wildcard, a private type
   (skipped) and a second assembly. Docs: "Declaring a requirement" on the capabilities page.
3. **Activation and the verdict.** DONE on branch `feat/1703-ws8s2` (issue #1704): `CapabilityActivationResolver`
   (explicit = key set to a value that turns the feature on, wildcard keys per configured entry; implicit = key
   absent and the generator-recorded declared default turns it on), `CapabilityVerdict` (satisfied means Ready;
   Provisionable fails an explicit requirement until slice 4 provisions), published on `ICapabilityRegistry` by
   the `Capabilities` step, the `required` gauge tag, the `whizbang_capabilities` health entry,
   `ICapabilityRequirementSource` with the configuration manifest as the first source, and
   `CapabilityVerdictEnforcer`, a hosted service that waits for the verdict in the background and on a failing
   line logs Critical, sets exit code 1 and stops the application. Not covered: a value set only in code
   (activation reads configuration and declared defaults). No library setting carries a requirement yet: the
   features the plan names (slices 7 to 9) do not exist. Original scope: Explicit vs implicit from configuration; the verdict line per
   required capability naming each feature and its key; the `required` gauge tag; the health entry.
   RED: one test per row of the validation table, including the exact line text.
4. **Provisioning runner and `AutoProvision`.** Generic runner with the three-level switch; the
   Postgres actions (`CREATE EXTENSION`, `ALTER EXTENSION UPDATE`) under the schema lock, outcome
   created / present / upgraded. RED: provisioning off still detects an operator-created extension;
   per-capability `false` overrides global `true`; an upgrade is recorded; NoPrivilege is reported,
   never thrown, on an implicit requirement. Docs: configuration reference (`Whizbang:Capabilities`).
5. **pg_trgm onto the pattern.** `OptionalExtensionBlocks.ApplyAsync` consults the capability before
   `CREATE EXTENSION`; the refusal classification stays as the fallback. RED: extend
   `OptionalExtensionBlocksTests.cs`. Docs: `fundamentals/perspectives/perspective-indexes.md`.
6. **pgvector onto the pattern.** The three generated sites in
   `EFCoreServiceRegistrationGenerator.cs` call one runtime helper instead of inline SQL; vector
   columns become a runtime requirement source (always explicit: the column cannot exist without
   the extension). RED: generator snapshot tests for the emitted call; integration test for the
   verdict on refusal. Docs: `fundamentals/perspectives/vector-fields.md`.
7. **pg_squeeze managed.** Requirement on `TableRewriteStrategy.PgSqueeze`; provisioning; the
   rewriter strategy itself is `storage-bloat.md` slice 3. RED: explicit PgSqueeze on a server
   without preload fails start with the preload fix text.
8. **pg_partman managed.** Requirement on `PartitionManagementStrategy.PgPartman`; `create_parent`
   on conversion and `run_maintenance` each maintenance cycle (the strategy itself is
   `storage-ledger-retention.md`). RED: `Auto` with pg_partman Ready uses it; NotPermitted falls back
   to the built-in manager and says so in the verdict.
9. **pg_repack managed, both run modes.** The client program becomes a probed prerequisite (found on
   `PATH` or at `ClientPath`, version read with `--version`). `InProcess` runs it from the maintainer
   duty inside the idle window; `External` leaves the queue to a job helper. RED: in-process mode with
   no client fails an explicit requirement and degrades an implicit one; external mode never starts a
   process; credentials never appear in the process arguments. Docs: the storage-maintenance page.
10. **Existing readers.** `GetExpensivePredicatesAsync` and the bloat estimate check the capability
    instead of catching every `PostgresException`. RED: Ready reads; NeedsServerChange returns empty
    without a query.
11. **Unknown keys (defect 8).** DONE on branch `feat/1490-unknown-config-keys` (issue #1490): the recorded
    manifest is embedded in Whizbang.Core and a hosted service registered by `AddWhizbang` checks it at
    start; removed keys are a table (`RemovedConfigurationKeys`) until slice 2's generator can carry
    `[RemovedConfigurationKey]`. Original scope: startup warning from the manifest; `[RemovedConfigurationKey]` for
    `Whizbang:WorkCoordinator:MaintenanceInterval` naming
    `Whizbang:Workers:Maintenance:IntervalMinutes`. RED: a removed key warns with its replacement; a
    typo under a known section warns; a dictionary entry does not; `Throw` fails start. Docs:
    configuration reference.

## Engineering standards

- Strict red before green (`ai-docs/tdd-strict.md`); every slice starts with a failing test.
- 100% of new lines and hand-written branches (`ai-docs/coverage-exclusions.md`).
- No reflection in library code (`ai-docs/aot-requirements.md`): attribute metadata is read only by
  the generator; runtime reads generated arrays.
- Generator rules: sealed value-type records, syntactic filter first
  (`src/Whizbang.Generators/ai-docs/performance-principles.md`).
- Public types carry `<docs>` and `<tests>` tags (`ai-docs/documentation-maintenance.md`).
- Any SQL change goes in a **new** migration (CI blocks edits to existing ones) and follows
  `src/Whizbang.Data.Postgres/Migrations/README.md`.
- pg_squeeze and pg_partman integration tests need a mirrored CI image with the extensions
  (`ai-docs/ci-image-mirror.md`).

## Verification

- On a managed server where pg_squeeze, pg_repack and pg_partman are installed but not allowlisted:
  the verdict shows vector, pg_trgm, pg_stat_statements as Ready and the three as NotPermitted, each
  naming the features and keys that need it.
- After the operator allowlists and preloads pg_squeeze and restarts, the next start reports
  `postgres/pg_squeeze: Ready, created` with no other change; with
  `Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision=false` it reports Provisionable and the
  fix instead.
- Setting `TableRewriteStrategy=PgSqueeze` on a server without it stops startup with the fix text.
- A deployment still carrying `MaintenanceInterval` logs one warning naming the replacement.

## Decisions (formerly open questions)

All decided on 2026-10-22 in #1672 (the owner accepted the recommendations).

1. **Unknown plus explicit.** When the probe can't tell whether an explicitly configured extension
   is usable, startup throws after a bounded retry. Explicit configuration means the feature is
   required.
2. **Unknown configuration keys.** 1.0 ships `Warn` as the default, with `Throw` available as an
   opt-in.
3. **Startup order.** The `Capabilities` step stays after `Migrate` in 1.0. A provider that needs no
   schema does not get an earlier step.
