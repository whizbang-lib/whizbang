# Startup and Registration Invariants

Six invariants that startup and DI registration must hold, each learned from a shipped defect
(issues #619, #620, #621, #630, #636 and the redacted-credential failure reported in the #619
thread). Read this before touching `AddWhizbangWorkers`, `DbContextInitializationRegistry`,
`PostgresDriverExtensions`, `EventSubscriptionDiscovery`, or notification connection resolution.

The common shape of all six: **the framework silently did less than the consumer asked for, and the
only visible symptom was far from the cause.** Every fix below turns a silence into either a refusal
with a reason or a log line that names the consequence.

---

## 1. `AddWhizbangWorkers` is idempotent by a marker, not by discipline

`AddWhizbang()` calls `AddWhizbangWorkers()`; the framework's own error messages tell consumers to
call it too. A second call is therefore the common case. Most registrations inside are `TryAdd`, but
the additive ones (`IStartupStep`, `IStartupStepObserver`, every hosted worker) doubled on a second
call: six startup steps, and `StartupStepOrderResolver` refused the duplicate names inside a
`BackgroundService`, where `StopHost` took the host down.

- A private marker instance is registered on the first call; a later call returns immediately.
- The six enumerable registrations also use `TryAddEnumerable` with implementation-typed descriptors,
  so a repeat of the *same* step is a no-op while two *different* steps sharing a name still reach the
  resolver's refusal (that is what the check is for).
- Test: `WorkerPipelineIdempotencyTests`. Note the hosted-service count really did double
  (measured 33 to 66); the marker is what fixes that, not the descriptor change.

## 2. Schema initialization is guarded per host, never per process

`DbContextInitializationRegistry` keys its "already initialized" guard on the `IServiceProvider` it is
given, in a `ConditionalWeakTable`. `EnsureWhizbangInitializedAsync` passes the host's **root**
provider (the registered callbacks create their own scopes), so the explicit call and the hosted
`WhizbangDatabaseInitializerService` share one key per host, and two hosts in one process each
initialize their own database.

The previous guard was a process-wide `static int`. Every host after the first was told "already
initialized" and started against an empty database; the first thing to hit it was the duty elector's
`record_capability`, surfaced as a Kestrel bind cancellation. Any test suite with a host per test
reproduced it on every test after the first. Tests: `DbContextInitializationRegistryTests`,
`WhizbangHostExtensionsTests`.

The `_initializers` list is still process-static (generated module initializers register one entry
per DbContext type); a host that does not register a given DbContext skips it inside the callback.

## 3. The library version is a build-time constant the driver always registers

`ILibraryVersionProvider` used to be registered only inside the generated turnkey callback, which
`DbContextRegistrationRegistry.InvokeRegistration` skips when the consumer already registered the
DbContext (a supported shape). No provider meant `ComputeVerdict(null, …)` read "unreadable", stood
down, and the host hung forever at `Host.StartAsync` with a narration many hosts silence.

- `Whizbang.Data.EFCore.Postgres.csproj` has an MSBuild target (`WhizbangGenerateLibraryVersionInfo`)
  that writes `LibraryVersionInfo.g.cs` with `public const string Value = "$(Version)"` before
  compile. No reflection. CI passes `-p:Version`, which is the same value the generator strips its own
  informational version to, so the ledger and the constant agree.
- `PostgresDriverExtensions` does `TryAddSingleton<ILibraryVersionProvider>` from it right after
  `InvokeRegistration`, so the generated registration (same value) or an explicit consumer
  registration wins.
- `ComputeVerdict(null, …)` now names the missing registration; unparseable is a separate message.
- Tests: `LibraryVersionRegistrationTests`, `StartupAssessorTests`.

## 4. Every coordinator query is schema-qualified from the model

`wh_service_config` is created as `__SCHEMA__.wh_service_config`. `GetLocalServiceIdAsync` was the one
query in `EFCoreWorkCoordinator` that named its table bare, so it resolved through `search_path`:
`42P01` once a minute from the integrity checkpoint worker on a non-public schema, and, worse, a
swallowed failure at publish time that stamped every envelope with an empty `SourceServiceId`. In a
database where `public` also has the table (the test database does), the bare query does not fail; it
reads the wrong row.

Rule: `GetSchemaWithFallback(model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA,
_logger)` + `BuildSchemaQualifiedName(schema, table)`, like every neighbor. Companion rule from
`OutboxDrainWorker`: a best-effort fallback that swallows an exception must log **what failed and what
is now untrue** (Warning, EventId 49: "every envelope this instance publishes will carry an empty
SourceServiceId, so downstream consumers will attribute those messages to themselves"). Tests:
`EFCoreWorkCoordinatorServiceIdTests` (integration, schema-scoped DbContext),
`OutboxDrainWorkerTests`.

## 5. An owned-and-subscribed namespace is refused, not discarded

`EventSubscriptionDiscovery` removes owned namespaces from the subscription set on purpose (a service
does not subscribe to what it publishes). A manual `SubscribeTo` on an owned namespace was therefore
silently dropped: no subscription, and Service Bus discards messages on a topic with no subscriptions.

- `OwnedNamespaceMatcher` holds the one hierarchical rule (exact, or child with a `.` boundary,
  case-insensitive, trailing dot tolerated). Discovery and the guard both call it.
- `RoutingOptions.ThrowIfSubscribedNamespaceIsOwned()` throws `InvalidOperationException` naming every
  offending namespace, the owned domain that claims it, and both remedies (drop the `SubscribeTo`, or
  `AbsorbNamespaces` to force the binding). Called from the `WithRouting` options factory at first
  resolution (the `ThrowIfRetirementIncomplete` seam) and from `DiscoverEventNamespaces` as defense in
  depth for hand-built options.
- Only **manual** subscriptions are checked; auto-discovered overlaps keep the silent exclusion,
  because that is the design and nothing the consumer wrote is being ignored.
- Three discovery tests that used a manual overlap to prove the exclusion were re-arranged onto
  auto-discovered namespaces with their names kept, because the docs cite those names.
- The other three private `_isOwnedNamespace` copies (`Dispatcher`, `ReceptorInvoker`,
  `TransportConsumerWorker`) were left alone: behavior-preserving to fold, but hot paths with no
  failing test to drive it. Candidate follow-up.

## 6. Notification credentials: borrow the data source when the string is redacted

Under `UseNpgsql(NpgsqlDataSource)` Npgsql redacts the password from every `ConnectionString` surface.
Auto-discovery of `INotificationDataSource` walked `IConfiguration:ConnectionStrings` for a
credential-bearing entry and stopped; a consumer with its data source in code and no
`ConnectionStrings` section got `DataSource = null`, every worker fell to the redacted string, and the
duty elector failed SASL inside the startup pipeline.

Precedence in `AddWhizbangPostgresNotifications` auto-discovery, first hit wins:

1. Explicit `Whizbang:Database` (`DirectConnectionString`, `ConnectionStringKey`) — dedicated pool.
2. First credential-bearing `ConnectionStrings:*` entry — dedicated pool.
3. `INotificationDataSourceFallback.GetDataSource()` — the DbContext's own data source, surfaced by
   the EF Core driver (`DbContextNotificationConnectionStringFallback` reads
   `NpgsqlOptionsExtension.DataSource`, EF1001 suppressed as the existing translator does). Borrowed.
4. A DI-registered `NpgsqlDataSource`. Borrowed.
5. Nothing: `DataSource = null`, string path, existing operator diagnostic.

Borrowed means `ownsDataSource: false` (never disposed by the notification stack) and used as-is: no
dedicated pool sizing, application-name stamping, keepalive tuning, or `search_path` application.
Logged once at Information. Multi-schema consumers that rely on a `search_path` should configure a
dedicated source. Tests: `NotificationDataSourceAutoDiscoveryTests`,
`DbContextNotificationConnectionStringFallbackTests`, `DutyElectionByoDataSourceE2ETests` (real
container, SCRAM-SHA-256).

---

## Verifying a change here

- Core: `cd tests/Whizbang.Core.Tests && dotnet run --no-build -- --treenode-filter "/*/*/<Class>/*"`.
- EF Core (Docker required): same in `tests/Whizbang.Data.EFCore.Postgres.Tests`.
- The plan with the RED/GREEN record: `plans/startup-hardening-619-620-621-630-636.md`.
- Docs pages: `fundamentals/dispatcher/routing#owned-and-subscribed`,
  `data/turnkey-initialization#idempotency`, `operations/startup/rolling-upgrades#assess`,
  `data/drivers#bring-your-own-dbcontext`, `messaging/work-coordinator#local-service-identity`,
  `operations/deployment/troubleshooting#workers-not-wired`.
