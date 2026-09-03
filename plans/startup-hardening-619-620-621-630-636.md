# Startup Hardening: issues #619, #620, #621, #630, #636 (+ redacted notification credentials)

## Context

Five open issues, all filed against 0.1024.0 and all reproduced on `develop` at e26cb1668
(2026-09-03), describe startup and registration defects that share one shape: the framework
silently does less than the consumer asked for, and the only visible symptom is far from the cause.

| Issue | Symptom | Root cause on develop |
|---|---|---|
| #619 | Host hangs forever at `Host.StartAsync` when the consumer registers its own DbContext | `ILibraryVersionProvider` is registered only inside the generated turnkey callback, which `DbContextRegistrationRegistry.InvokeRegistration` skips when the DbContext is already registered. The assessor reads a null version as "unreadable" and stands down. |
| #620 | Second host in one process starts against an uninitialized database | `DbContextInitializationRegistry` guards with a process-wide `static int _initialized`, keyed on nothing. |
| #621 | Calling `AddWhizbangWorkers()` after `AddWhizbang()` stops the host | The three `IStartupStep` and three `IStartupStepObserver` registrations use `AddSingleton`, so the resolver sees duplicate step names and refuses. |
| #630 | `42P01: relation "wh_service_config" does not exist` once a minute; every published envelope carries `SourceServiceId = Guid.Empty` on a non-public schema | `EFCoreWorkCoordinator.GetLocalServiceIdAsync` is the only unqualified query in the coordinator; `OutboxDrainWorker` swallows the failure without a log line. |
| #636 | A namespace that is both owned and manually subscribed gets no events subscription; Service Bus drops the messages silently | `EventSubscriptionDiscovery` removes owned namespaces from the subscription set by design (self-echo prevention). The manual `SubscribeTo` is silently discarded. |
| (from #619 thread) | Duty elector fails SASL auth: "No password has been provided" when the DbContext is configured with `UseNpgsql(NpgsqlDataSource)` | Npgsql redacts the password from every `ConnectionString` surface. Auto-discovery of `INotificationDataSource` only walks `IConfiguration:ConnectionStrings`; it never reuses the consumer's data source, so the elector falls to the redacted string. |

Decision for #636: **fail at startup**, mirroring the existing `ThrowIfRetirementIncomplete` guard
(thrown from the `WithRouting` options factory at first resolution, and again from
`EventSubscriptionDiscovery` as defense in depth). An explicit `SubscribeTo` on an owned namespace
is a contradiction the framework cannot honor; `AbsorbNamespaces` is the documented escape hatch for
"I want the binding anyway". Auto-discovered overlaps keep today's silent exclusion (that is the
design intent and is not a declaration the consumer wrote).

## Progress log

| Date | Item | Status | Notes |
|---|---|---|---|
| 2026-09-03 | Discovery + plan | complete | All six items verified on develop e26cb1668. Branch `fix/startup-hardening-619-620-621-630-636` off `origin/develop`. |
| 2026-09-03 | A. #621 idempotent registrations | GREEN (Core) | RED: `WorkerPipelineIdempotencyTests` 4 failures (6 steps, 6 observers, hosted count doubled 33→66, resolver "Assess registered more than once"). GREEN: marker guard at the top of `AddWhizbangWorkers` + `TryAddEnumerable` for the six enumerable registrations. Finding: hosted workers DID double on a second call; the marker fixes that too. |
| 2026-09-03 | B. #636 own+subscribe guard | GREEN (Core) | RED: `RoutingBuilderExtensionsTests`, `EventSubscriptionDiscoveryTests` (no throw); wave 2 compile RED for `OwnedNamespaceMatcher` and `ThrowIfSubscribedNamespaceIsOwned`. GREEN: new `OwnedNamespaceMatcher`, guard on `RoutingOptions`, called from the factory and from discovery; three discovery tests re-arranged onto auto-discovered namespaces (names kept, docs cite them). |
| 2026-09-03 | C. #630 schema-qualified service id + logged fallback | GREEN | RED: `OutboxDrainWorkerTests` log test (no line); `EFCoreWorkCoordinatorServiceIdTests` read PUBLIC's row instead of the service schema's (`ef-red.log`, the "worse" case: wrong identity, no error). GREEN: `GetSchemaWithFallback` + `BuildSchemaQualifiedName` in `GetLocalServiceIdAsync`; Warning EventId 49 in the drain worker's catch. |
| 2026-09-03 | D. #620 per-provider init guard | GREEN | RED: two providers → 1 callback; two hosts → 1 (`ef-red.log`). GREEN: `ConditionalWeakTable<IServiceProvider,…>` keyed guard; `EnsureWhizbangInitializedAsync` passes `host.Services`. |
| 2026-09-03 | E. #619 library version always registered | GREEN | RED: `ILibraryVersionProvider` null under a consumer-owned DbContext; `ComputeVerdict(null)` reason lacked the registration name (`ef-red.log`). GREEN: MSBuild target emits `LibraryVersionInfo.g.cs` from `$(Version)`; driver `TryAddSingleton<ILibraryVersionProvider>`; `ComputeVerdict(null)` names the missing registration. |
| 2026-09-03 | F. notification data source reuse (redacted credentials) | GREEN | RED: three auto-discovery tests (data source not reused) and the E2E elector failed with the reporter's exact error, "No password has been provided but the backend requires one (in SASL/SCRAM-SHA-256)" (`ef-red.log`). GREEN: `INotificationDataSourceFallback`; fallback reads `NpgsqlOptionsExtension.DataSource` (EF1001 suppressed, same as the existing translator); auto-discovery borrows fallback → DI `NpgsqlDataSource`, logged once. RED pass method: `git stash push` of the six production files whose fixes define no new symbols, rebuild, run, `git stash pop`, `git diff` byte-identical to the pre-stash snapshot. |
| 2026-09-03 | G. docs site + `<docs>`/`<tests>` tags + ai-docs summary | in progress | Docs worktree `docs/startup-hardening-619-636` off `origin/main` (develop there is stale; recent PRs land on main). Edited: routing.md (`#owned-and-subscribed`), turnkey-initialization.md, rolling-upgrades.md, drivers.md (`#bring-your-own-dbcontext`), troubleshooting.md. Pending: work-coordinator.md (#630), map regeneration, ai-docs summary. |
| 2026-09-03 | H. full build, format, tests, coverage, PR | in progress | Library PR whizbang#651 (draft, all 27 CI checks green), docs PR whizbang-lib.github.io#540 (draft, build green). Local full suite: 20,034 tests, 20,020 passed, 14 skipped, 2 projects failed (see I). Sample fixture repaired (`ECommerce.Lifecycle.Integration.Tests` did not compile on develop). Coverage: every changed line covered except the deliberate double-check in `GetDataSource`, since restructured to a single check under the lock; three small tests added for the null-logger and dot-only-domain branches. |
| 2026-09-03 | I. flaky tests surfaced by the coverage run | in progress | `UngatedWorkerAdoptionTests.PerspectiveMigration_…` and `ClaimWorkerGateCadenceTests.GateFlipsToAvailable_…` each failed once under the coverage-instrumented parallel run; 5/5 green in isolation; CI green. Both were timing-based (a 300 ms "nothing yet" delay; a 2 s "prompt poll" budget). Rewritten signal-based: an observable `ISchemaReadyGate` double reports when the worker is parked on it; the cadence test uses an hour-long interval so only the flip can produce a poll. The cadence rewrite exposed a product gap: `ClaimWorker._onGateAvailabilityChanged` only released the semaphore, which cannot interrupt the spacing nap, so an outage edge could wait out a nap. Fix: gate transitions cancel the nap like new-work signals do (`_wakeNow`); completion-feedback wakes still cannot. RED observed before the fix (`flake-red.log`). |

## Goals

1. Every fix is test-first: a RED test that fails on develop for the reported reason, then GREEN.
2. Zero reflection in library code (test code may keep the pre-existing reflection resets).
3. 100% line and branch coverage of every changed or added production member.
4. Every public API touched carries `<docs>` and `<tests>` tags; the docs site describes the new
   behavior on the page each tag points at.
5. One PR, one commit per item, `Fixes #N` for all five issues in the PR body.

## Design

### A. #621 `AddWhizbangWorkers` is idempotent

`WorkerPipelineExtensions.AddWhizbangWorkers`: register the three `IStartupStepObserver`s and the
three `IStartupStep`s through `TryAddEnumerable` with descriptors typed by implementation
(`ServiceDescriptor.Singleton<IStartupStep, AssessStartupStep>(sp => ...)`), so a second call is a
no-op and two *different* steps sharing a name still trip the resolver. `AddHostedService<T>` is
already `TryAddEnumerable` under the hood; a test documents that.

Tests: `tests/Whizbang.Core.Tests/Workers/WorkerPipelineIdempotencyTests.cs`.

### B. #636 owned + subscribed namespace is refused

- `Whizbang.Core.Routing.OwnedNamespaceMatcher.IsOwned(string ns, IEnumerable<string> owned)`:
  the hierarchical rule (exact or child with a `.` boundary, case-insensitive) as one helper.
  `EventSubscriptionDiscovery` uses it in place of its inline copy.
- `RoutingOptions.ThrowIfSubscribedNamespaceIsOwned()` (internal): every manual
  `SubscribedNamespaces` entry that is owned and not in `AbsorbedNamespaces` is a contradiction.
  Throws `InvalidOperationException` naming each namespace, the owned domain it matched, and the
  two remedies (drop the `SubscribeTo`, or `AbsorbNamespaces` to force the binding).
- Called from the `WithRouting` options factory (after `ThrowIfRetirementIncomplete`) and from
  `EventSubscriptionDiscovery.DiscoverEventNamespaces`.
- Existing discovery tests that used a manual `SubscribeTo` overlap to prove the exclusion rule are
  re-arranged onto auto-discovered namespaces, keeping their names (the docs cite them).

Tests: `RoutingOptionsTests`, `RoutingBuilderExtensionsTests`, `EventSubscriptionDiscoveryTests`,
new `OwnedNamespaceMatcherTests`.

### C. #630 service id query is schema-qualified, and the fallback is logged

- `EFCoreWorkCoordinator.GetLocalServiceIdAsync` resolves the schema with `GetSchemaWithFallback`
  and qualifies with `BuildSchemaQualifiedName`, like every neighbor.
- `OutboxDrainWorker`: the `catch (Exception)` around the lookup logs a Warning (own EventId)
  naming what failed and the consequence (every envelope this instance publishes carries an empty
  `SourceServiceId`; downstream consumers attribute those messages to themselves).

Tests: `EFCoreWorkCoordinatorServiceIdTests` (integration, schema-scoped DbContext),
`OutboxDrainWorkerTests` (log emission when the lookup throws).

### D. #620 initialization guard is per service provider

`DbContextInitializationRegistry`: replace the static `int` with a
`ConditionalWeakTable<IServiceProvider, InitializationState>` (the same structure
`DbContextRegistrationRegistry` already uses for `_invoked`). `EnsureWhizbangInitializedAsync`
passes `host.Services` (the root) so the explicit call and the hosted
`WhizbangDatabaseInitializerService` share one key per host.

Tests: `DbContextInitializationRegistryTests` (two providers both initialize; same provider skips
and logs), `WhizbangHostExtensionsTests` (two hosts in one process both initialize).

### E. #619 the library version is always registered

- MSBuild target in `Whizbang.Data.EFCore.Postgres.csproj` writes
  `LibraryVersionInfo.g.cs` with `internal static class LibraryVersionInfo { public const string
  Value = "$(Version)"; }` before compile. CI already passes `-p:Version`, which is the same value
  the generator strips to, so the ledger and the instance rows still agree.
- `PostgresDriverExtensions` registers `TryAddSingleton<ILibraryVersionProvider>` from that
  constant after `InvokeRegistration`, so the generated registration (when it ran) wins and the
  bring-your-own-DbContext path is no longer missing it.
- `ComputeVerdict(null, ...)` gets its own reason ("no ILibraryVersionProvider is registered")
  distinct from an unparseable value.

Tests: `PostgresDriverExtensionsTests` (BYO DbContext still gets the provider),
`LibraryVersionInfoTests`, `StartupAssessorTests`.

### F. notification data source reuse when credentials are redacted

- `Whizbang.Data.Postgres.Notifications.INotificationDataSourceFallback` (`NpgsqlDataSource?
  GetDataSource()`).
- `DbContextNotificationConnectionStringFallback` implements it by reading
  `NpgsqlOptionsExtension.DataSource` off the DbContext options (the same technique the generated
  schema initializer already uses for VACUUM), cached.
- Auto-discovery in `AddWhizbangPostgresNotifications`: after the configuration walk finds nothing,
  probe the fallback, then a DI-registered `NpgsqlDataSource`; wrap with `ownsDataSource: false`.
- `PostgresDriverExtensions` registers the fallback under both interfaces (one instance).

Tests: `DbContextNotificationConnectionStringFallbackTests` (`GetDataSource` cases),
`NotificationDataSourceAutoDiscoveryTests` (precedence + ownership), integration:
`PgDutyElector` acquires a duty under `UseNpgsql(NpgsqlDataSource)` with no notification
configuration.

## Verification

- Core changes: `cd tests/Whizbang.Core.Tests && dotnet run --no-build -- --treenode-filter "/*/*/<Class>/*"`.
- EF Core changes: same in `tests/Whizbang.Data.EFCore.Postgres.Tests` (Docker required).
- Full: `pwsh scripts/Run-Tests.ps1 -Mode Ai -LogFile <file>`; coverage via `-Coverage`.
- `dotnet format` before every commit.

## Out of scope (with reasons)

- Refactoring the three private `_isOwnedNamespace` copies in `Dispatcher`, `ReceptorInvoker`,
  and `TransportConsumerWorker` onto the new helper: behavior-preserving but touches hot paths with
  no failing test to drive it; tracked as a follow-up.
- Changing `StartupReadyService`'s narration category (raised in #619): the hang it narrates is the
  documented fail-closed posture; with the version provider always present the trigger is gone.
- Making a failed exclusive step stop the host instead of narrating (raised in #620): develop
  already converts elector exceptions into a failed step after repeated failures.
