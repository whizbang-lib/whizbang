# Test separation inventory (#1264)

## Purpose

Phase 2 of #1264 moves every test that is not a unit test out of the Unit-type projects into a project of the
right type. This inventory is that worklist: every top-level type in every Unit-type project, classified as
Unit, Component, Integration or Other, with the construct and line that put it there. The machine-readable
list is [`test-separation-inventory.csv`](test-separation-inventory.csv) (one row per top-level type: Project,
File, Class, Line, Kind, Tests, Category, Constructs, Reasons, Via, Note). Below the marker at the end of this
file are the counts per project and the non-unit types per project.

Categories (the owner's definitions):

- **Unit:** one deterministic flow; injected fake clock; no background threads or hosted workers; no real I/O.
- **Component:** real workers, threads or waits in one process, no external infrastructure.
- **Integration:** containers or real infrastructure.
- **Other:** anything else that is not unit, such as the real file system, a child process or the real clock
  without infrastructure.

## How it was generated

```
pwsh .github/scripts/Get-TestPurity.ps1 -Inventory
```

`.github/scripts/Get-TestPurity.ps1` is the canonical generator: it writes the CSV and regenerates everything
below the marker, keeping this hand-written part. Without `-Inventory` it prints the purity report, which the
repository test in `.github/scripts/tests/Get-TestPurity.Tests.ps1` also writes on every run (report mode
until phase 3 sets `$script:PurityEnforced`).

The committed copy was produced by a line-by-line port of the script that reads its regular expressions out of
the script itself, because PowerShell could not run where the inventory was produced. The first run of the
command above regenerates both files; a difference would be a defect in the port, not in the rules.

## Rules

The scan blanks comments, string and character literals and using directives first (so a construct named in a
doc comment, or in C# source held in a string, is not matched), splits each file at its top-level type
declarations, and attributes each construct to the type it occurs in, with its line. Nested types belong to the
type that encloses them; attributes belong to the type they precede.

| Construct (rule id) | What it matches | Category |
|---|---|---|
| Container | Testcontainers, a container builder, `SharedPostgresContainer`/`SharedRabbitMqContainer`, `PerTestDatabaseFactory`, `DockerExecutable`, `.WithImage(` | Integration |
| RealHttp | `new HttpClient()` or `new HttpClient { ... }` with no test handler | Integration |
| StartAsync | `.StartAsync(` on a worker or host | Component |
| HostedService | a type deriving from `BackgroundService` or `IHostedService` | Component |
| HostBuilder | `Host.Create*Builder(`, `new HostBuilder(`, `WebApplication.Create*(` | Component |
| TestServer | `new TestServer(`, `UseTestServer(`, `WebApplicationFactory<`, `GetTestServer(`/`GetTestClient(` | Component |
| ThreadSleep | `Thread.Sleep(` | Component |
| TaskDelay | `Task.Delay(` unless the delay is `Timeout.Infinite`, `Timeout.InfiniteTimeSpan` or `-1` | Component |
| NewThread | `new Thread(` | Component |
| TaskRun | `Task.Run(`, `Task.Factory.StartNew(` | Component |
| ThreadPool | `ThreadPool.*` | Component |
| Parallel | `Parallel.For`, `ForEach`, `ForEachAsync`, `Invoke` | Component |
| Timer | `new Timer(`, `new System.Threading.Timer(`, `new PeriodicTimer(` | Component |
| TimeoutWait | a timeout used as a wait: `.WaitAsync(<timeout>)`, `.Wait(<timeout>)`, `.CancelAfter(`, `new CancellationTokenSource(<timeout>)` | Component |
| CrossThreadSignal | `RunContinuationsAsynchronously` in a test class (a signal completed from another thread); not counted in a double that merely offers one | Component |
| RealClock | `TimeProvider.System` | Other |
| Stopwatch | `Stopwatch.StartNew(`, `GetTimestamp(`, `GetElapsedTime(`, `new Stopwatch(` | Other |
| FileSystem | any `File.*`/`Directory.*` call except `Exists`, `Path.GetTempPath`/`GetTempFileName`, `new FileInfo/DirectoryInfo/FileStream/FileSystemWatcher(` | Other |
| Process | `Process.Start(`, `new Process`, `new ProcessStartInfo` | Other |
| Socket | `new TcpListener/TcpClient/UdpClient/Socket(` | Other |
| NetworkClient | `new ConnectionFactory`, `new NpgsqlConnection(`, `new NpgsqlDataSourceBuilder(`, `NpgsqlDataSource.Create(` (handed to code that dials it, with no infrastructure behind it) | Other |

**Precedence.** A type with several constructs takes the worst: Integration > Component > Other > Unit.

**References.** A type that names a helper (a top-level type with no `[Test]`) of the same project takes the
helper's category, to a fixed point, and lists it in Via. Not followed: a name declared by more than one
top-level type in the project, a name the type declares itself (a nested type shadowing a helper), and a test
class (other tests reference one for a nested data type, not for its behavior).

**Partial classes.** The parts of a partial class (same namespace and name, several files) take the category
of the whole class, since they move together; each part lists `partial <Class> (<Category>)` in Via.

**Judgments, all deliberate:**

- `DateTime.UtcNow`/`DateTimeOffset.UtcNow` are not flagged: here they make timestamps for test data, and a
  flow is timing-dependent only when it compares them with elapsed time, which Stopwatch and
  `TimeProvider.System` cover.
- A bare `File.Exists`/`Directory.Exists` probe is not flagged: the shared compile helpers in
  Whizbang.Generators.Tests use one to find a reference assembly in the test's own output folder. Flagging it
  marked 187 generator test classes Other through those helpers.
- Constructing an Azure SDK client (`ServiceBusClient`, `BlobServiceClient`) is not flagged: construction
  never dials, and the tests that do it are DI-registration tests.
- In-memory SQLite (`Data Source=:memory:`, Whizbang.Data.Tests and Whizbang.Data.Schema.Tests) is an
  in-process engine with no I/O, so it is unit.

## Notable findings

1. **Totals.** 35 Unit-type projects, 2,503 top-level types, 2,230 test classes holding 19,931 `[Test]`
   methods. 461 test classes (4,375 test methods) are not unit tests: 420 Component (3,856 methods),
   1 Integration (3), 40 Other (516). 14 helper types are non-unit as well. 17 of the 35 projects hold a
   non-unit test class, 2 more hold only a non-unit helper (finding 3), and 16 are already pure.
2. **Whizbang.Core.Tests holds 350 of the 461**, all Component except 6 Other. 200 of the 350 are in
   `Workers/`; their rows carry the note "phase 2 waits for #1259", since #1259 edits worker tests. The
   files #1259 itself edits are `PerspectiveWorkerAffinityHoldWatchdogTests.cs`,
   `PerspectiveWorkerCoverageTests.cs`, `PerspectiveWorkerDeepPathChannelTests.BranchCoverage.cs`,
   `PerspectiveWorkerDeepPathDrainTests.BranchCoverage.cs` and `PerspectiveWorkerDeepPathDrainTests.cs`, all
   Component, and the two partial classes among them span 6 and 5 files that must move together.
3. **Unit projects tagged Docker.** `samples/ECommerce/tests/ECommerce.BFF.API.Tests` and
   `samples/ECommerce/tests/ECommerce.InventoryWorker.Tests` declare the Unit type with the tags
   `Integration;Docker`. Every compiled test class in them is unit. Their only infrastructure code is three
   Postgres container helpers in `TestHelpers/` (`DatabaseTestHelper`, `EFCoreTestHelper`), used only by
   `*.cs.bak` files, which are not compiled. Nothing moves; the tags (and the dead helpers) are what is wrong.
4. **`samples/ECommerce/ECommerce.IntegrationTests`** is a Unit-type project whose name says integration; its
   one test class is unit by these rules.
5. **Other splits into two kinds, which suggests one more category.**
   - *Real file system or child process, deterministic, no infrastructure* (32 classes): Whizbang.Migrate.Tests
     (23: temporary directories, and three that run `git`), Whizbang.CLI.Tests (the audit workspace),
     Whizbang.Generators.Tests (temporary docs repositories), Whizbang.Testing.Tests (trace files), and two
     Core.Tests source-invariant tests that read the repository's own source files
     (`InjectedExtensibilityPointsAreDocumentedTests`, `FlushApiSourceInvariantTests`). These are neither
     Component nor Integration; a separate type (for example **System**, for tests of the process's real
     environment) would hold them without diluting either.
   - *Real time without threads* (6 classes): `TimeProvider.System` or Stopwatch timing in a single flow, such
     as `SagaClaimPruneStepTests`, `PgWorkAvailablePollSourceAdaptiveIntervalTests`,
     `TransportPublishStrategyThrottleRetryTests` and `OsvClientTests`. They are timing-dependent like
     Component tests, so they could join Component instead.
   - *Network attempts against unreachable endpoints* (2): `CoordinatorConnectionScopeTests` (Npgsql to a
     non-routable address with a one-second timeout) and `RabbitMQConnectionRetryTests` (a broker host that does
     not resolve). Real network I/O with no infrastructure behind it.
6. **A debug leftover.** `ReceptorDiscoveryGeneratorTests` writes `/tmp/test-dispatcher.g.cs`, a shared
   absolute path that two parallel runs race on. It is the only construct in that class (81 tests); deleting the
   debug line would leave the class unit, which is a fix rather than a move.
7. **Ambiguous classifications, for review before moving:**
   - *StartAsync only* (32 classes): some start a hosted service whose `StartAsync` is synchronous validation or
     configuration, with no worker behind it (`TagPolicyValidatorTests`, `TagPolicyValidatorRouteNamespaceTests`,
     `TransportNamespaceRoutingRegistrationTests`, `RegistrationValidationStartupTests`,
     `PerspectiveRowRetentionConfiguratorTests`, `TagCoalesceConfigurationBinderTests`). The rule cannot tell a
     validator from a worker, so they read Component.
   - *TaskDelay only* (30 classes): a short real delay in a double to simulate asynchronous work
     (`VoidReceptorTests`, `ReceptorInvokerLoggingTests`), or to let a UUIDv7 timestamp tick
     (`WhizbangIdTypesTests`). A real timer, so Component by the rules, but `Task.Yield` or a fake clock would
     make them unit.
   - *HostBuilder only* (1): `HostConfigurationDisposalTests` builds a web host and never starts it.
   - *CrossThreadSignal only* (16): signal-driven concurrency tests that wait with no timeout, such as
     `CircuitBreakerCoverageTests` (two concurrent calls, interleaving fixed by signals) and
     `RabbitMQSubscriptionCoverageTests` (the code under test runs a fire-and-forget task). These are the
     owner's Component definition exactly; without this rule they read as unit.
8. **What a scan of the test text cannot see.** Concurrency that lives entirely in the code under test and is
   awaited deterministically, with no marker in the test, reads as unit (for example
   `LifecycleTrackingStateCoverageTests`, which drains detached tasks). Process-global state, such as the
   environment variables `WhizbangBannerTests` sets, is not a construct here. The phase 2 count proof (TRX
   before and after) is unaffected; these only decide where a class goes.

## Spot-check

Classes were read by hand in each category, and the rules were corrected where they misclassified:

- **Integration:** all 4 types read (the 3 ECommerce container helpers and `QueryPlanCaptureTests`, which
  seeds a real Postgres through `SharedPostgresContainer`). All correct.
- **Component:** 21 read (17 test classes and 4 doubles), 13 of them single-construct classes. Correct by the definitions; the ambiguous
  groups in finding 7 came from this sample.
- **Other:** 18 read (network clients, Azure SDK clients, real clock, Stopwatch, the generator compile helpers,
  temporary docs repositories, child processes). Two rule fixes came from it: `ServiceBusClient` construction
  (DI-registration tests in Whizbang.Transports.AzureServiceBus.Tests that never dial) was dropped; the `File.Exists` probe in the compile helpers
  (187 generator classes) was dropped; the debug write in finding 6 stays flagged.
- **Unit:** 8 read closely, plus a search of every unit-classified class for constructs the rules do not cover
  (`RunAsync`, `ExecuteAsync`, `Start()`, `Task.WhenAny`, channels, SQLite, environment variables, locks,
  `Interlocked`, `RunContinuationsAsynchronously`). Three rule fixes came from it: partial classes now unify
  (one part of `PerspectiveWorkerDeepPathChannelTests` read unit while its other five parts are Component);
  a cross-thread signal in a test class is now Component (16 classes); and propagation no longer follows test
  classes or names a type declares itself (a nested `FakeTransport` had picked up the category of an unrelated
  top-level `FakeTransport`; `IntegrityAuditWorkerPriorityTests` had picked up `IntegrityAuditWorkerTests`
  through a nested event type). `lock` and `Interlocked` were rejected as markers: 188 unit classes use them,
  mostly as habit in doubles.

<!-- Generated by .github/scripts/Get-TestPurity.ps1 -Inventory. Everything below this line is regenerated; edit above it. -->

## Counts per project

Test classes (top-level types with at least one `[Test]`) by category. A non-unit helper is a top-level type with no test that uses a non-unit construct; it moves with the classes that use it.

| Project | Unit | Component | Integration | Other | Total | Non-unit helpers |
|---|---:|---:|---:|---:|---:|---:|
| ECommerce.Contracts.Tests | 10 | 0 | 0 | 0 | 10 | 0 |
| ECommerce.IntegrationTests | 1 | 0 | 0 | 0 | 1 | 0 |
| ECommerce.BFF.API.Tests | 9 | 0 | 0 | 0 | 9 | 2 |
| ECommerce.InventoryWorker.Tests | 5 | 0 | 0 | 0 | 5 | 1 |
| ECommerce.NotificationWorker.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| ECommerce.OrderService.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| ECommerce.PaymentWorker.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| ECommerce.ShippingWorker.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.CLI.Tests | 5 | 0 | 0 | 0 | 5 | 0 |
| Whizbang.Core.Tests | 1169 | 1 | 0 | 0 | 1170 | 0 |
| Whizbang.Data.Schema.Tests | 30 | 0 | 0 | 0 | 30 | 0 |
| Whizbang.Data.Tests | 11 | 0 | 0 | 0 | 11 | 0 |
| Whizbang.Documentation.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.Execution.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.Generators.Tests | 251 | 0 | 0 | 0 | 251 | 0 |
| Whizbang.Hosting.AspNet.Tests | 19 | 0 | 0 | 0 | 19 | 0 |
| Whizbang.Hosting.Azure.ServiceBus.Tests | 2 | 0 | 0 | 0 | 2 | 0 |
| Whizbang.Hosting.RabbitMQ.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.LanguageServer.Tests | 18 | 0 | 0 | 0 | 18 | 0 |
| Whizbang.Migrate.Tests | 39 | 0 | 0 | 0 | 39 | 0 |
| Whizbang.Observability.Tests | 23 | 0 | 0 | 0 | 23 | 0 |
| Whizbang.Offloads.AzureBlob.Tests | 5 | 0 | 0 | 0 | 5 | 0 |
| Whizbang.Offloads.InMemory.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.Policies.Tests | 6 | 0 | 0 | 0 | 6 | 0 |
| Whizbang.Sagas.Tests | 39 | 0 | 0 | 0 | 39 | 0 |
| Whizbang.SignalR.Tests | 6 | 0 | 0 | 0 | 6 | 0 |
| Whizbang.Testing.Tests | 13 | 0 | 0 | 0 | 13 | 0 |
| Whizbang.Transports.AzureServiceBus.Tests | 56 | 0 | 0 | 0 | 56 | 0 |
| Whizbang.Transports.FastEndpoints.Tests | 12 | 0 | 0 | 0 | 12 | 0 |
| Whizbang.Transports.HotChocolate.Tests | 20 | 0 | 0 | 0 | 20 | 0 |
| Whizbang.Transports.Mutations.Tests | 3 | 0 | 0 | 0 | 3 | 0 |
| Whizbang.Transports.RabbitMQ.Tests | 23 | 0 | 0 | 0 | 23 | 0 |
| Whizbang.Transports.Tests | 12 | 0 | 0 | 0 | 12 | 0 |
| **Total** | 1795 | 1 | 0 | 0 | 1796 | 3 |

## Worklist: non-unit types by project

Every top-level type that is not unit-pure, in file order. Tests = 0 marks a helper. The CSV has the line of every construct.

### ECommerce.Contracts.Tests

All 10 types are unit-pure.

### ECommerce.IntegrationTests

All 2 types are unit-pure.

### ECommerce.BFF.API.Tests

2 of 20 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| DatabaseTestHelper | `samples/ECommerce/tests/ECommerce.BFF.API.Tests/TestHelpers/DatabaseTestHelper.cs` | 0 | Integration | Container, NetworkClient |  |  |
| EFCoreTestHelper | `samples/ECommerce/tests/ECommerce.BFF.API.Tests/TestHelpers/EFCoreTestHelper.cs` | 0 | Integration | Container, NetworkClient |  |  |

### ECommerce.InventoryWorker.Tests

1 of 9 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| DatabaseTestHelper | `samples/ECommerce/tests/ECommerce.InventoryWorker.Tests/TestHelpers/DatabaseTestHelper.cs` | 0 | Integration | Container, NetworkClient |  |  |

### ECommerce.NotificationWorker.Tests

All 1 types are unit-pure.

### ECommerce.OrderService.Tests

All 1 types are unit-pure.

### ECommerce.PaymentWorker.Tests

All 1 types are unit-pure.

### ECommerce.ShippingWorker.Tests

All 1 types are unit-pure.

### Whizbang.CLI.Tests

All 6 types are unit-pure.

### Whizbang.Core.Tests

1 of 1285 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| MessageSecurityContextProviderTests | `tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs` | 17 | Component | TaskDelay |  |  |

### Whizbang.Data.Schema.Tests

All 30 types are unit-pure.

### Whizbang.Data.Tests

All 17 types are unit-pure.

### Whizbang.Documentation.Tests

All 1 types are unit-pure.

### Whizbang.Execution.Tests

All 1 types are unit-pure.

### Whizbang.Generators.Tests

All 258 types are unit-pure.

### Whizbang.Hosting.AspNet.Tests

All 19 types are unit-pure.

### Whizbang.Hosting.Azure.ServiceBus.Tests

All 4 types are unit-pure.

### Whizbang.Hosting.RabbitMQ.Tests

All 1 types are unit-pure.

### Whizbang.LanguageServer.Tests

All 18 types are unit-pure.

### Whizbang.Migrate.Tests

All 39 types are unit-pure.

### Whizbang.Observability.Tests

All 23 types are unit-pure.

### Whizbang.Offloads.AzureBlob.Tests

All 5 types are unit-pure.

### Whizbang.Offloads.InMemory.Tests

All 1 types are unit-pure.

### Whizbang.Policies.Tests

All 8 types are unit-pure.

### Whizbang.Sagas.Tests

All 45 types are unit-pure.

### Whizbang.SignalR.Tests

All 6 types are unit-pure.

### Whizbang.Testing.Tests

All 24 types are unit-pure.

### Whizbang.Transports.AzureServiceBus.Tests

All 85 types are unit-pure.

### Whizbang.Transports.FastEndpoints.Tests

All 21 types are unit-pure.

### Whizbang.Transports.HotChocolate.Tests

All 46 types are unit-pure.

### Whizbang.Transports.Mutations.Tests

All 13 types are unit-pure.

### Whizbang.Transports.RabbitMQ.Tests

All 28 types are unit-pure.

### Whizbang.Transports.Tests

All 16 types are unit-pure.
