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
| Whizbang.CLI.Tests | 4 | 0 | 0 | 4 | 8 | 1 |
| Whizbang.Core.Tests | 1149 | 344 | 0 | 6 | 1499 | 7 |
| Whizbang.Data.Schema.Tests | 30 | 0 | 0 | 0 | 30 | 0 |
| Whizbang.Data.Tests | 11 | 0 | 0 | 0 | 11 | 0 |
| Whizbang.Documentation.Tests | 1 | 1 | 0 | 0 | 2 | 0 |
| Whizbang.Execution.Tests | 1 | 7 | 0 | 0 | 8 | 0 |
| Whizbang.Generators.Tests | 250 | 0 | 0 | 4 | 254 | 0 |
| Whizbang.Hosting.AspNet.Tests | 19 | 10 | 0 | 0 | 29 | 0 |
| Whizbang.Hosting.Azure.ServiceBus.Tests | 1 | 2 | 0 | 0 | 3 | 1 |
| Whizbang.Hosting.RabbitMQ.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.LanguageServer.Tests | 18 | 0 | 0 | 0 | 18 | 0 |
| Whizbang.Migrate.Tests | 39 | 0 | 0 | 23 | 62 | 0 |
| Whizbang.Observability.Tests | 21 | 3 | 0 | 0 | 24 | 0 |
| Whizbang.Offloads.AzureBlob.Tests | 5 | 0 | 0 | 0 | 5 | 0 |
| Whizbang.Offloads.InMemory.Tests | 1 | 0 | 0 | 0 | 1 | 0 |
| Whizbang.Partitioning.Tests | 1 | 1 | 0 | 0 | 2 | 0 |
| Whizbang.Policies.Tests | 6 | 0 | 0 | 0 | 6 | 0 |
| Whizbang.Sagas.Tests | 38 | 3 | 0 | 1 | 42 | 0 |
| Whizbang.Sequencing.Tests | 0 | 1 | 0 | 0 | 1 | 0 |
| Whizbang.SignalR.Tests | 6 | 0 | 0 | 0 | 6 | 0 |
| Whizbang.Testing.Tests | 12 | 8 | 1 | 1 | 22 | 0 |
| Whizbang.Transports.AzureServiceBus.Tests | 56 | 10 | 0 | 0 | 66 | 0 |
| Whizbang.Transports.FastEndpoints.Tests | 12 | 0 | 0 | 0 | 12 | 0 |
| Whizbang.Transports.HotChocolate.Tests | 20 | 0 | 0 | 0 | 20 | 0 |
| Whizbang.Transports.Mutations.Tests | 3 | 0 | 0 | 0 | 3 | 0 |
| Whizbang.Transports.RabbitMQ.Tests | 23 | 0 | 0 | 1 | 24 | 0 |
| Whizbang.Transports.Tests | 12 | 8 | 0 | 0 | 20 | 0 |
| **Total** | 1769 | 398 | 1 | 40 | 2208 | 12 |

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

5 of 10 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| AuditCommandTests | `tests/Whizbang.CLI.Tests/Audit/AuditCommandTests.cs` | 22 | Other | RealClock | AuditWorkspace (Other) |  |
| AuditReportTests | `tests/Whizbang.CLI.Tests/Audit/AuditReportTests.cs` | 12 | Other | FileSystem |  |  |
| AuditWorkspace | `tests/Whizbang.CLI.Tests/Audit/AuditWorkspace.cs` | 0 | Other | FileSystem |  |  |
| OsvClientTests | `tests/Whizbang.CLI.Tests/Audit/OsvClientTests.cs` | 17 | Other | RealClock |  |  |
| ProjectAssetsReaderTests | `tests/Whizbang.CLI.Tests/Audit/ProjectAssetsReaderTests.cs` | 17 | Other | FileSystem | AuditWorkspace (Other) |  |

### Whizbang.Core.Tests

357 of 1626 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| AsyncTimeoutHelperNonGenericTests | `tests/Whizbang.Core.Tests/Async/AsyncTimeoutHelperNonGenericTests.cs` | 4 | Component | CrossThreadSignal |  |  |
| AsyncTimeoutHelperTests | `tests/Whizbang.Core.Tests/Async/AsyncTimeoutHelperTests.cs` | 10 | Component | TaskDelay, TaskRun, CrossThreadSignal |  |  |
| RegistrationValidationStartupTests | `tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationStartupTests.cs` | 4 | Component | StartAsync |  |  |
| DebuggerAwareClockCoverageTests | `tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockCoverageTests.cs` | 9 | Component | TimeoutWait, CrossThreadSignal |  |  |
| DebuggerAwareClockCpuSamplingTests | `tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockCpuSamplingTests.cs` | 17 | Component | TaskDelay, Stopwatch |  |  |
| DebuggerAwareClockTests | `tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs` | 55 | Component | TaskDelay, CrossThreadSignal, Stopwatch |  |  |
| InertConcurrencyStartupReporterBranchCoverageTests | `tests/Whizbang.Core.Tests/Diagnostics/InertConcurrencyStartupReporterBranchCoverageTests.cs` | 2 | Component | StartAsync |  |  |
| InertConcurrencyStartupReporterTests | `tests/Whizbang.Core.Tests/Diagnostics/InertConcurrencyStartupReporterTests.cs` | 6 | Component | StartAsync |  |  |
| DispatcherLocalInvokeAndSyncTimingTests | `tests/Whizbang.Core.Tests/Dispatcher/DispatcherLocalInvokeAndSyncTimingTests.cs` | 7 | Component | TaskDelay, Stopwatch |  |  |
| DispatcherSyncTests | `tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs` | 5 | Component | TaskDelay |  |  |
| DispatcherTests | `tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs` | 44 | Component | TaskDelay, CrossThreadSignal |  |  |
| InjectedExtensibilityPointsAreDocumentedTests | `tests/Whizbang.Core.Tests/Documentation/InjectedExtensibilityPointsAreDocumentedTests.cs` | 2 | Other | FileSystem |  |  |
| ParallelExecutorCoverageTests | `tests/Whizbang.Core.Tests/Execution/ParallelExecutorCoverageTests.cs` | 2 | Component | StartAsync |  |  |
| TypeDefinitionReconcilerHostedServiceTests | `tests/Whizbang.Core.Tests/Fingerprint/TypeDefinitionReconcilerHostedServiceTests.cs` | 3 | Component | StartAsync, CrossThreadSignal |  |  |
| HealthProbeMustAlwaysAnswerTests | `tests/Whizbang.Core.Tests/Health/HealthProbeMustAlwaysAnswerTests.cs` | 5 | Component | TimeoutWait |  |  |
| DebugAwareStopwatchTests | `tests/Whizbang.Core.Tests/Lifecycle/DebugAwareStopwatchTests.cs` | 13 | Component | TaskDelay |  |  |
| LifecycleCoordinatorTests | `tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs` | 52 | Component | TaskRun |  |  |
| BatchWorkCoordinatorStrategyFullCoverageTests | `tests/Whizbang.Core.Tests/Messaging/BatchWorkCoordinatorStrategyFullCoverageTests.cs` | 16 | Component | TaskRun, TimeoutWait, CrossThreadSignal |  |  |
| BatchWorkCoordinatorStrategyTests | `tests/Whizbang.Core.Tests/Messaging/BatchWorkCoordinatorStrategyTests.cs` | 52 | Component | TimeoutWait, CrossThreadSignal |  |  |
| DeferredOutboxChannelTests | `tests/Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs` | 9 | Component | TaskRun |  |  |
| EventStoreOrderingInvariantTests | `tests/Whizbang.Core.Tests/Messaging/EventStoreOrderingInvariantTests.cs` | 3 | Component | TaskRun |  |  |
| FlushApiSourceInvariantTests | `tests/Whizbang.Core.Tests/Messaging/FlushApiSourceInvariantTests.cs` | 4 | Other | FileSystem |  |  |
| IUnitOfWorkStrategyContractTests | `tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs` | 8 | Component | TaskDelay |  |  |
| ImmediateUnitOfWorkStrategyTests | `tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs` | 10 | Component | TaskDelay |  |  |
| InMemoryRequestResponseStoreCancellationTests | `tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreCancellationTests.cs` | 2 | Component | TimeoutWait |  |  |
| InMemoryRequestResponseStoreTests | `tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs` | 8 | Component | TaskDelay |  |  |
| InboxChannelWriterTests | `tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterTests.cs` | 10 | Component | TimeoutWait |  |  |
| IntervalUnitOfWorkStrategyTests | `tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs` | 12 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| IntervalWorkCoordinatorStrategyCoverageTests | `tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyCoverageTests.cs` | 19 | Component | TimeoutWait, CrossThreadSignal |  |  |
| IntervalWorkCoordinatorStrategyEdgeCaseTests | `tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyEdgeCaseTests.cs` | 41 | Component | TaskDelay, TaskRun, TimeoutWait, CrossThreadSignal, Stopwatch |  |  |
| IntervalWorkCoordinatorStrategyTests | `tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs` | 19 | Component | TaskDelay, TimeoutWait |  |  |
| LifecycleContextAccessorTests | `tests/Whizbang.Core.Tests/Messaging/LifecycleContextAccessorTests.cs` | 5 | Component | ThreadSleep, TaskRun |  |  |
| LifecycleInvocationHelperTests | `tests/Whizbang.Core.Tests/Messaging/LifecycleInvocationHelperTests.cs` | 22 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| LifecycleStageTriggerIndependenceTests | `tests/Whizbang.Core.Tests/Messaging/LifecycleStageTriggerIndependenceTests.cs` | 4 | Component | TimeoutWait, CrossThreadSignal |  |  |
| OrderedStreamProcessorTests | `tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs` | 23 | Component | TimeoutWait, CrossThreadSignal |  |  |
| PerspectiveChannelWriterTests | `tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs` | 7 | Component | TaskRun |  |  |
| ProcessingModeTests | `tests/Whizbang.Core.Tests/Messaging/ProcessingModeTests.cs` | 19 | Component | TaskRun |  |  |
| ReceptorFiringObserverTests | `tests/Whizbang.Core.Tests/Messaging/ReceptorFiringObserverTests.cs` | 4 | Component | TimeoutWait, CrossThreadSignal |  |  |
| ReceptorInvokerLoggingTests | `tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerLoggingTests.cs` | 4 | Component | TaskDelay |  |  |
| ReceptorInvokerTagFireAndForgetTests | `tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTagFireAndForgetTests.cs` | 2 | Component | TimeoutWait, CrossThreadSignal |  |  |
| ScopedUnitOfWorkStrategyTests | `tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs` | 12 | Component | TaskDelay |  |  |
| StreamAffinityWorkCoordinatorStrategyTests | `tests/Whizbang.Core.Tests/Messaging/StreamAffinityWorkCoordinatorStrategyTests.cs` | 7 | Component | TimeoutWait |  |  |
| WorkCoordinatorGateInteractiveReserveTests | `tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateInteractiveReserveTests.cs` | 9 | Component | TimeoutWait |  |  |
| WorkCoordinatorGateTests | `tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateTests.cs` | 11 | Component | TaskDelay, TimeoutWait |  |  |
| CrossServiceFlagsE2ETests | `tests/Whizbang.Core.Tests/MultiService/CrossServiceFlagsE2ETests.cs` | 2 | Component | StartAsync |  |  |
| DirectedMessageE2ETests | `tests/Whizbang.Core.Tests/MultiService/DirectedMessageE2ETests.cs` | 1 | Component | StartAsync |  |  |
| MultiServiceHarnessIdentityTests | `tests/Whizbang.Core.Tests/MultiService/MultiServiceHarnessIdentityTests.cs` | 1 | Component | StartAsync |  |  |
| PublisherFlipMigrationE2ETests | `tests/Whizbang.Core.Tests/MultiService/PublisherFlipMigrationE2ETests.cs` | 5 | Component | StartAsync |  |  |
| StreamIntegrityRedeliveryE2ETests | `tests/Whizbang.Core.Tests/MultiService/StreamIntegrityRedeliveryE2ETests.cs` | 1 | Component | StartAsync |  |  |
| NotifySubscriptionRegistryCoverageTests | `tests/Whizbang.Core.Tests/Notifications/NotifySubscriptionRegistryCoverageTests.cs` | 3 | Component | NewThread |  |  |
| NotifySubscriptionRegistryTests | `tests/Whizbang.Core.Tests/Notifications/NotifySubscriptionRegistryTests.cs` | 10 | Component | Parallel |  |  |
| PgCommitOrderStamperIterationDiagnosticsTests | `tests/Whizbang.Core.Tests/Notifications/PgCommitOrderStamperIterationDiagnosticsTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| PgNotificationStackStartupGateTests | `tests/Whizbang.Core.Tests/Notifications/PgNotificationStackStartupGateTests.cs` | 8 | Component | StartAsync, TaskDelay, CrossThreadSignal |  |  |
| PgSharedNotifyConnectionProbeTests | `tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionProbeTests.cs` | 8 | Component | TimeoutWait |  |  |
| PgSharedNotifyConnectionReconnectDiagnosticsTests | `tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionReconnectDiagnosticsTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| PgSharedNotifyConnectionTests | `tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionTests.cs` | 10 | Component | StartAsync |  |  |
| PgWorkNotificationListenerTests | `tests/Whizbang.Core.Tests/Notifications/PgWorkNotificationListenerTests.cs` | 12 | Component | StartAsync |  |  |
| BacklogAgeDutyTests | `tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs` | 13 | Component | StartAsync, CrossThreadSignal |  |  |
| BacklogAgeWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Observability/BacklogAgeWorkerBranchCoverageTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| DeadLetterMetricsEmissionTests | `tests/Whizbang.Core.Tests/Observability/DeadLetterMetricsEmissionTests.cs` | 2 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal, Stopwatch |  |  |
| NotifyDebounceStatsCollectorBranchCoverageTests | `tests/Whizbang.Core.Tests/Observability/NotifyDebounceStatsCollectorBranchCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| NotifyDebounceStatsCollectorTests | `tests/Whizbang.Core.Tests/Observability/NotifyDebounceStatsCollectorTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| PassiveCounterTests | `tests/Whizbang.Core.Tests/Observability/PassiveCounterTests.cs` | 11 | Component | Parallel |  |  |
| TableStatisticsCollectorBranchTests | `tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorBranchTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| TableStatisticsCollectorCoverageTests | `tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| TableStatisticsCollectorTests | `tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| UnobservedExceptionDiagnosticsTests | `tests/Whizbang.Core.Tests/Observability/UnobservedExceptionDiagnosticsTests.cs` | 11 | Component | StartAsync, TaskDelay, TaskRun |  |  |
| WhizbangStartupLoggerTests | `tests/Whizbang.Core.Tests/Observability/WhizbangStartupLoggerTests.cs` | 2 | Component | StartAsync |  |  |
| PerspectiveApplyCoordinatorDiagnosticsTests | `tests/Whizbang.Core.Tests/Perspectives/PerspectiveApplyCoordinatorDiagnosticsTests.cs` | 4 | Component | TimeoutWait, CrossThreadSignal |  |  |
| PerspectiveRewindCompletionGapTests | `tests/Whizbang.Core.Tests/Perspectives/PerspectiveRewindCompletionGapTests.cs` | 3 | Component | TaskRun, TimeoutWait, CrossThreadSignal |  |  |
| PerspectiveRowRetentionConfiguratorTests | `tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowRetentionConfiguratorTests.cs` | 2 | Component | StartAsync |  |  |
| PerspectiveTableRedirectTests | `tests/Whizbang.Core.Tests/Perspectives/PerspectiveTableRedirectTests.cs` | 5 | Component | TaskRun, CrossThreadSignal |  |  |
| RegistrySnapshotConcurrencyTests | `tests/Whizbang.Core.Tests/Perspectives/RegistrySnapshotConcurrencyTests.cs` | 2 | Component | TaskRun |  |  |
| RewindLiveApplyRaceTests | `tests/Whizbang.Core.Tests/Perspectives/RewindLiveApplyRaceTests.cs` | 3 | Component | TaskDelay, TaskRun, TimeoutWait, CrossThreadSignal |  |  |
| CrossCommandPerspectiveSyncTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/CrossCommandPerspectiveSyncTests.cs` | 5 | Component | TaskDelay, TaskRun, CrossThreadSignal, Stopwatch |  |  |
| DispatcherSingletonTrackerTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/DispatcherSingletonTrackerTests.cs` | 5 | Component | TaskDelay, TaskRun |  |  |
| DispatcherSyncTrackingVerificationTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/DispatcherSyncTrackingVerificationTests.cs` | 8 | Component | TaskDelay, TaskRun |  |  |
| EventCompletionAwaiterTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/EventCompletionAwaiterTests.cs` | 17 | Component | TaskDelay |  |  |
| PerspectiveSyncAwaiterAppliedTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterAppliedTests.cs` | 15 | Component | TimeoutWait, CrossThreadSignal |  |  |
| PerspectiveSyncAwaiterStreamTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterStreamTests.cs` | 10 | Other | Stopwatch |  |  |
| PerspectiveSyncAwaiterTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs` | 50 | Component | TimeoutWait |  |  |
| PerspectiveSyncAwaiterTrackerTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTrackerTests.cs` | 12 | Component | TaskDelay, TaskRun, Stopwatch |  |  |
| PerspectiveSyncSignalerTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs` | 10 | Component | TimeoutWait |  |  |
| ScopedEventTrackerTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs` | 16 | Component | TaskRun |  |  |
| SyncContextAccessorTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/SyncContextAccessorTests.cs` | 14 | Component | TaskDelay, TaskRun |  |  |
| SyncEventTrackerTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/SyncEventTrackerTests.cs` | 64 | Component | TaskDelay, TaskRun |  |  |
| UserScenarioReproductionTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/UserScenarioReproductionTests.cs` | 3 | Component | TaskRun, CrossThreadSignal |  |  |
| WaitForStreamAsyncIntegrationTests | `tests/Whizbang.Core.Tests/Perspectives/Sync/WaitForStreamAsyncIntegrationTests.cs` | 6 | Component | TaskDelay, TaskRun |  |  |
| SyncEventTrackerCoverageTests | `tests/Whizbang.Core.Tests/Perspectives/SyncEventTrackerCoverageTests.cs` | 4 | Component | TaskRun |  |  |
| PooledValueTaskSourceContractTests | `tests/Whizbang.Core.Tests/Pooling/PooledValueTaskSourceContractTests.cs` | 5 | Component | TimeoutWait, CrossThreadSignal |  |  |
| ClaimWorkerPriorityBatchHookTests | `tests/Whizbang.Core.Tests/Priority/ClaimWorkerPriorityBatchHookTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| ConsumerPriorityClassificationTests | `tests/Whizbang.Core.Tests/Priority/ConsumerPriorityClassificationTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| InboxDispatchWorkerPriorityContextTests | `tests/Whizbang.Core.Tests/Priority/InboxDispatchWorkerPriorityContextTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| PriorityHooksTests | `tests/Whizbang.Core.Tests/Priority/PriorityHooksTests.cs` | 14 | Component | TaskRun |  |  |
| PriorityOnTheWireEndToEndTests | `tests/Whizbang.Core.Tests/Priority/PriorityOnTheWireEndToEndTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| ReceptorTests | `tests/Whizbang.Core.Tests/Receptors/ReceptorTests.cs` | 8 | Component | TaskDelay |  |  |
| VoidReceptorTests | `tests/Whizbang.Core.Tests/Receptors/VoidReceptorTests.cs` | 5 | Component | TaskDelay |  |  |
| AssemblyRegistryTests | `tests/Whizbang.Core.Tests/Registry/AssemblyRegistryTests.cs` | 12 | Component | TaskRun |  |  |
| CircuitBreakerCoverageTests | `tests/Whizbang.Core.Tests/Resilience/CircuitBreakerCoverageTests.cs` | 2 | Component | CrossThreadSignal |  |  |
| CircuitBreakerTests | `tests/Whizbang.Core.Tests/Resilience/CircuitBreakerTests.cs` | 12 | Component | TaskDelay |  |  |
| StreamRateLimiterTests | `tests/Whizbang.Core.Tests/Resilience/StreamRateLimiterTests.cs` | 15 | Component | TaskDelay, TaskRun |  |  |
| SubscriptionRetryHelperCoverageTests | `tests/Whizbang.Core.Tests/Resilience/SubscriptionRetryHelperCoverageTests.cs` | 2 | Component | TimeoutWait, CrossThreadSignal |  |  |
| SubscriptionRetryHelperTests | `tests/Whizbang.Core.Tests/Resilience/SubscriptionRetryHelperTests.cs` | 17 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| LifecyclePhaseWorkerCoverageTests | `tests/Whizbang.Core.Tests/RunControl/LifecyclePhaseWorkerCoverageTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| LifecyclePhaseWorkerTests | `tests/Whizbang.Core.Tests/RunControl/LifecyclePhaseWorkerTests.cs` | 2 | Component | StartAsync, TaskDelay, CrossThreadSignal |  |  |
| WhizbangLifecycleCoordinatorTests | `tests/Whizbang.Core.Tests/RunControl/WhizbangLifecycleCoordinatorTests.cs` | 5 | Component | CrossThreadSignal |  |  |
| MessageContextAccessorTests | `tests/Whizbang.Core.Tests/Security/MessageContextAccessorTests.cs` | 10 | Component | TaskRun |  |  |
| MessageSecurityContextProviderTests | `tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs` | 17 | Component | TaskDelay |  |  |
| ScopeContextAccessorInitiatingContextTests | `tests/Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs` | 20 | Component | TaskDelay, TaskRun |  |  |
| ScopeContextAccessorTests | `tests/Whizbang.Core.Tests/Security/ScopeContextAccessorTests.cs` | 7 | Component | TaskDelay, TaskRun |  |  |
| SecurityContextHelperCoverageTests | `tests/Whizbang.Core.Tests/Security/SecurityContextHelperCoverageTests.cs` | 2 | Component | TaskDelay, CrossThreadSignal |  |  |
| InMemorySignalTransportTests | `tests/Whizbang.Core.Tests/Signals/InMemorySignalTransportTests.cs` | 3 | Component | StartAsync |  |  |
| PgWorkAvailablePollSourceAdaptiveIntervalTests | `tests/Whizbang.Core.Tests/Signals/PgWorkAvailablePollSourceAdaptiveIntervalTests.cs` | 4 | Other | RealClock |  |  |
| PollSignalSourceIdleBackoffTests | `tests/Whizbang.Core.Tests/Signals/PollSignalSourceIdleBackoffTests.cs` | 6 | Component | StartAsync |  |  |
| PollSignalSourceTests | `tests/Whizbang.Core.Tests/Signals/PollSignalSourceTests.cs` | 13 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| SignalBusHostedServiceBranchCoverageTests | `tests/Whizbang.Core.Tests/Signals/SignalBusHostedServiceBranchCoverageTests.cs` | 4 | Component | StartAsync, TimeoutWait |  |  |
| SignalBusHostedServiceCoverageTests | `tests/Whizbang.Core.Tests/Signals/SignalBusHostedServiceCoverageTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| SignalBusHostingTests | `tests/Whizbang.Core.Tests/Signals/SignalBusHostingTests.cs` | 3 | Component | StartAsync, CrossThreadSignal |  |  |
| SignalBusProbeBackoffTests | `tests/Whizbang.Core.Tests/Signals/SignalBusProbeBackoffTests.cs` | 7 | Component | StartAsync, CrossThreadSignal |  |  |
| SignalBusProbeTests | `tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs` | 5 | Component | StartAsync, CrossThreadSignal |  |  |
| SignalBusRegistrationTests | `tests/Whizbang.Core.Tests/Signals/SignalBusRegistrationTests.cs` | 4 | Component | StartAsync |  |  |
| SignalBusTests | `tests/Whizbang.Core.Tests/Signals/SignalBusTests.cs` | 16 | Component | StartAsync |  |  |
| DutyHolderWorkerTests | `tests/Whizbang.Core.Tests/Startup/DutyHolderWorkerTests.cs` | 21 | Component | StartAsync, CrossThreadSignal |  |  |
| DutyShutdownReleaseServiceTests | `tests/Whizbang.Core.Tests/Startup/DutyShutdownReleaseServiceTests.cs` | 4 | Component | StartAsync |  |  |
| StandbyWatcherTests | `tests/Whizbang.Core.Tests/Startup/StandbyWatcherTests.cs` | 23 | Component | StartAsync, CrossThreadSignal |  |  |
| StartupPipelineHooksTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineHooksTests.cs` | 9 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| StartupPipelineResilienceTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineResilienceTests.cs` | 4 | Component | StartAsync |  |  |
| StartupPipelineRunnerCoverageTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineRunnerCoverageTests.cs` | 1 | Component | TimeoutWait, CrossThreadSignal |  |  |
| StartupPipelineRunnerDutyTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineRunnerDutyTests.cs` | 8 | Component | TaskDelay |  |  |
| StartupPipelineWiringTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineWiringTests.cs` | 7 | Component | StartAsync, TaskDelay, TimeoutWait |  |  |
| StartupPipelineWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Startup/StartupPipelineWorkerBranchCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait |  |  |
| StartupReadinessCoverageTests | `tests/Whizbang.Core.Tests/Startup/StartupReadinessCoverageTests.cs` | 2 | Component | TaskDelay |  |  |
| StartupReadyCompositeTests | `tests/Whizbang.Core.Tests/Startup/StartupReadyCompositeTests.cs` | 19 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| StartupReadyServiceBranchCoverageTests | `tests/Whizbang.Core.Tests/Startup/StartupReadyServiceBranchCoverageTests.cs` | 1 | Component | TimeoutWait |  |  |
| StartupWiringAuditTests | `tests/Whizbang.Core.Tests/Startup/StartupWiringAuditTests.cs` | 8 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| TagCoalesceConfigurationBinderTests | `tests/Whizbang.Core.Tests/Tags/TagCoalesceConfigurationBinderTests.cs` | 6 | Component | StartAsync |  |  |
| TagPolicyValidatorRouteNamespaceTests | `tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorRouteNamespaceTests.cs` | 9 | Component | StartAsync |  |  |
| TagPolicyValidatorTests | `tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs` | 14 | Component | StartAsync |  |  |
| TransportNamespaceRoutingRegistrationTests | `tests/Whizbang.Core.Tests/Tags/TransportNamespaceRoutingRegistrationTests.cs` | 5 | Component | StartAsync |  |  |
| ScheduleWorkerTests | `tests/Whizbang.Core.Tests/Temporal/ScheduleWorkerTests.cs` | 17 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  |  |
| TestTransport | `tests/Whizbang.Core.Tests/Transports/TransportLifecycleTests.cs` | 0 | Component | TaskDelay |  |  |
| CorrelationIdTests | `tests/Whizbang.Core.Tests/ValueObjects/CorrelationIdTests.cs` | 13 | Component | TaskDelay |  |  |
| IdentityValueObjectTests | `tests/Whizbang.Core.Tests/ValueObjects/IdentityValueObjectTests.cs` | 9 | Component | ThreadSleep, TaskDelay |  |  |
| MessageIdAdditionalTests | `tests/Whizbang.Core.Tests/ValueObjects/MessageIdAdditionalTests.cs` | 15 | Component | TaskDelay |  |  |
| TrackedGuidLockChangeLevelTests | `tests/Whizbang.Core.Tests/ValueObjects/TrackedGuidLockChangeLevelTests.cs` | 4 | Component | TaskRun |  |  |
| TrackedGuidMonotonicityTests | `tests/Whizbang.Core.Tests/ValueObjects/TrackedGuidMonotonicityTests.cs` | 4 | Component | TaskRun |  |  |
| TrackedGuidTests | `tests/Whizbang.Core.Tests/ValueObjects/TrackedGuidTests.cs` | 49 | Component | TaskDelay |  |  |
| Uuid7GeneratorTests | `tests/Whizbang.Core.Tests/ValueObjects/Uuid7GeneratorTests.cs` | 22 | Component | TaskRun |  |  |
| WhizbangIdCoverageTests | `tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdCoverageTests.cs` | 7 | Component | TaskDelay |  |  |
| WhizbangIdTests | `tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdTests.cs` | 17 | Component | TaskDelay |  |  |
| WhizbangIdTypesTests | `tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdTypesTests.cs` | 13 | Component | TaskDelay |  |  |
| BackgroundStageDispatchCoverageTests | `tests/Whizbang.Core.Tests/Workers/BackgroundStageDispatchCoverageTests.cs` | 3 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| BackgroundStageDispatchTests | `tests/Whizbang.Core.Tests/Workers/BackgroundStageDispatchTests.cs` | 6 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| BackupTickCoordinatorBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/BackupTickCoordinatorBranchCoverageTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| BackupTickCoordinatorCoverageTests | `tests/Whizbang.Core.Tests/Workers/BackupTickCoordinatorCoverageTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal, RealClock |  | phase 2 waits for #1259 |
| BackupTickCoordinatorStateMachineTests | `tests/Whizbang.Core.Tests/Workers/BackupTickCoordinatorStateMachineTests.cs` | 4 | Component | StartAsync, TaskDelay, CrossThreadSignal |  | phase 2 waits for #1259 |
| BatchFlusherCoverageTests | `tests/Whizbang.Core.Tests/Workers/BatchFlusherCoverageTests.cs` | 4 | Component | CrossThreadSignal |  | phase 2 waits for #1259 |
| BatchFlusherRetryTests | `tests/Whizbang.Core.Tests/Workers/BatchFlusherRetryTests.cs` | 2 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| BatchFlusherTests | `tests/Whizbang.Core.Tests/Workers/BatchFlusherTests.cs` | 7 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| ClaimChurnFeedbackTests | `tests/Whizbang.Core.Tests/Workers/ClaimChurnFeedbackTests.cs` | 8 | Component | TaskRun |  | phase 2 waits for #1259 |
| ClaimWorkerAcquisitionBoundsTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerAcquisitionBoundsTests.cs` | 21 | Component | StartAsync, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| ClaimWorkerAttemptAccountingTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerAttemptAccountingTests.cs` | 14 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerBranchCoverageTests.cs` | 9 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerBusWakeTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerBusWakeTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerCoverageTests.cs` | 9 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerDoorbellLivenessTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerDoorbellLivenessTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerDrainLingerTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerDrainLingerTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerGateCadenceTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerGateCadenceTests.cs` | 11 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerLifecycleTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerLifecycleTests.cs` | 11 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerPerspectiveDoorbellTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerPerspectiveDoorbellTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerReemissionBackoffTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerReemissionBackoffTests.cs` | 6 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerRegistrationTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerRegistrationTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerSignalCoalescingTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerSignalCoalescingTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ClaimWorkerTests | `tests/Whizbang.Core.Tests/Workers/ClaimWorkerTests.cs` | 9 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| CoalesceShipWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerBranchCoverageTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| CoalesceShipWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerCoverageTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| CoalesceShipWorkerTests | `tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs` | 27 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| CoordinatorConnectionScopeTests | `tests/Whizbang.Core.Tests/Workers/CoordinatorConnectionScopeTests.cs` | 7 | Other | NetworkClient |  | phase 2 waits for #1259 |
| CursorInversionDetectorTests | `tests/Whizbang.Core.Tests/Workers/CursorInversionDetectorTests.cs` | 24 | Component | TaskDelay |  | phase 2 waits for #1259 |
| DeadLetterCanaryCampaignTests | `tests/Whizbang.Core.Tests/Workers/DeadLetterCanaryCampaignTests.cs` | 24 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| DeadLetterRecoveryWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerBranchCoverageTests.cs` | 15 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| DeadLetterRecoveryWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| DeadLetterRecoveryWorkerTests | `tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs` | 38 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| DefaultBackupTickRegistrarTests | `tests/Whizbang.Core.Tests/Workers/DefaultBackupTickRegistrarTests.cs` | 6 | Component | StartAsync |  | phase 2 waits for #1259 |
| DrainWorkerIdleSignalTests | `tests/Whizbang.Core.Tests/Workers/DrainWorkerIdleSignalTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| EventIdSignalingLogger | `tests/Whizbang.Core.Tests/Workers/EventIdSignalingLogger.cs` | 0 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| FailureFlushWorkerTests | `tests/Whizbang.Core.Tests/Workers/FailureFlushWorkerTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| HeartbeatWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerCoverageTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| HeartbeatWorkerLifecycleSignalsTests | `tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerLifecycleSignalsTests.cs` | 6 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| HeartbeatWorkerTests | `tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerTests.cs` | 5 | Component | StartAsync, TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| IdleActivityTouchHookBinderTests | `tests/Whizbang.Core.Tests/Workers/IdleActivityTouchHookBinderTests.cs` | 6 | Component | StartAsync |  | phase 2 waits for #1259 |
| InboxDeserializeCacheCapConcurrencyTests | `tests/Whizbang.Core.Tests/Workers/InboxDeserializeCacheCapConcurrencyTests.cs` | 5 | Component | TaskRun |  | phase 2 waits for #1259 |
| InboxDispatchWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerBranchCoverageTests.cs` | 14 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerCompositeCommitTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerCompositeCommitTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerCoverageTests.cs` | 10 | Component | StartAsync, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| InboxDispatchWorkerGapTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerGapTests.cs` | 31 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerLifecycleGatingTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerLifecycleGatingTests.cs` | 11 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerLifecycleIntegrationTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerLifecycleIntegrationTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerParallelismTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerParallelismTests.cs` | 4 | Component | StartAsync, TaskDelay, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerSchedulingTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerSchedulingTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDispatchWorkerTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerTests.RepairTraffic.cs` | 4 | Component | StartAsync, TimeoutWait | partial InboxDispatchWorkerTests (Component) | phase 2 waits for #1259 |
| InboxDispatchWorkerTests | `tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerTests.cs` | 15 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal, Stopwatch | partial InboxDispatchWorkerTests (Component) | phase 2 waits for #1259 |
| InboxDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxDrainWorkerCoverageTests.BranchCoverage.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal | EventIdSignalingLogger (Component); partial InboxDrainWorkerCoverageTests (Component) | phase 2 waits for #1259 |
| InboxDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxDrainWorkerCoverageTests.TransientFailure.cs` | 1 | Component | StartAsync, TimeoutWait | EventIdSignalingLogger (Component); partial InboxDrainWorkerCoverageTests (Component) | phase 2 waits for #1259 |
| InboxDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxDrainWorkerCoverageTests.cs` | 6 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | partial InboxDrainWorkerCoverageTests (Component) | phase 2 waits for #1259 |
| InboxDrainWorkerGapTests | `tests/Whizbang.Core.Tests/Workers/InboxDrainWorkerGapTests.cs` | 15 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxDrainWorkerTests | `tests/Whizbang.Core.Tests/Workers/InboxDrainWorkerTests.cs` | 9 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxHandlerWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/InboxHandlerWorkerCoverageTests.cs` | 2 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxHandlerWorkerTests | `tests/Whizbang.Core.Tests/Workers/InboxHandlerWorkerTests.cs` | 12 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| InboxPrePublishGateForensicPreservationTests | `tests/Whizbang.Core.Tests/Workers/InboxPrePublishGateForensicPreservationTests.cs` | 5 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| IntegrityAuditWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerCoverageTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| IntegrityAuditWorkerLoopTests | `tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerLoopTests.cs` | 3 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| IntegrityAuditWorkerTests | `tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerTests.cs` | 28 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| IntegrityCheckpointWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerCoverageTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| IntegrityCheckpointWorkerTests | `tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs` | 20 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| LeaseRenewalWorkerCapTests | `tests/Whizbang.Core.Tests/Workers/LeaseRenewalWorkerCapTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| LeaseRenewalWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/LeaseRenewalWorkerCoverageTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| LifecycleExceptionInvariantTests | `tests/Whizbang.Core.Tests/Workers/LifecycleExceptionInvariantTests.LogLevel.cs` | 2 | Component |  | partial LifecycleExceptionInvariantTests (Component) | phase 2 waits for #1259 |
| LifecycleExceptionInvariantTests | `tests/Whizbang.Core.Tests/Workers/LifecycleExceptionInvariantTests.PayloadTooLarge.cs` | 4 | Component | CrossThreadSignal | partial LifecycleExceptionInvariantTests (Component) | phase 2 waits for #1259 |
| LifecycleExceptionInvariantTests | `tests/Whizbang.Core.Tests/Workers/LifecycleExceptionInvariantTests.cs` | 3 | Component |  | partial LifecycleExceptionInvariantTests (Component) | phase 2 waits for #1259 |
| MaintenanceWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerCoverageTests.cs` | 9 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| MaintenanceWorkerLifecycleTests | `tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerLifecycleTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| MaintenanceWorkerTests | `tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerTests.cs` | 6 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OrphanInboxJanitorBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/OrphanInboxJanitorBranchCoverageTests.cs` | 2 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| OrphanInboxJanitorCoverageTests | `tests/Whizbang.Core.Tests/Workers/OrphanInboxJanitorCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| OrphanInboxJanitorTests | `tests/Whizbang.Core.Tests/Workers/OrphanInboxJanitorTests.cs` | 10 | Component | StartAsync, TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| OutboxCompletionFlushWorkerTests | `tests/Whizbang.Core.Tests/Workers/OutboxCompletionFlushWorkerTests.cs` | 3 | Component | StartAsync, TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| OutboxDrainWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerBranchCoverageTests.cs` | 13 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OutboxDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerCoverageTests.TransientFailure.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal | EventIdSignalingLogger (Component); partial OutboxDrainWorkerCoverageTests (Component) | phase 2 waits for #1259 |
| OutboxDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerCoverageTests.cs` | 10 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | partial OutboxDrainWorkerCoverageTests (Component) | phase 2 waits for #1259 |
| OutboxDrainWorkerGapTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerGapTests.cs` | 26 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OutboxDrainWorkerStreamRunTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerStreamRunTests.cs` | 12 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OutboxDrainWorkerTests | `tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerTests.cs` | 26 | Component | StartAsync, TaskDelay, TaskRun, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OutboxPublishWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/OutboxPublishWorkerBranchCoverageTests.cs` | 13 | Component | StartAsync, TimeoutWait, CrossThreadSignal | EventIdSignalingLogger (Component) | phase 2 waits for #1259 |
| OutboxPublishWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/OutboxPublishWorkerCoverageTests.cs` | 6 | Component | StartAsync, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| OutboxPublishWorkerDlqPromotionTests | `tests/Whizbang.Core.Tests/Workers/OutboxPublishWorkerDlqPromotionTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| OutboxPublishWorkerErrorPathTests | `tests/Whizbang.Core.Tests/Workers/OutboxPublishWorkerErrorPathTests.cs` | 18 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| OutboxPublishWorkerTests | `tests/Whizbang.Core.Tests/Workers/OutboxPublishWorkerTests.cs` | 6 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal, Stopwatch |  | phase 2 waits for #1259 |
| OutstandingBudgetChurnFeedbackTests | `tests/Whizbang.Core.Tests/Workers/OutstandingBudgetChurnFeedbackTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerStreamSerializerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/PerStreamSerializerBranchCoverageTests.cs` | 1 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerStreamSerializerCoverageTests | `tests/Whizbang.Core.Tests/Workers/PerStreamSerializerCoverageTests.cs` | 5 | Component | TaskDelay, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerStreamSerializerTests | `tests/Whizbang.Core.Tests/Workers/PerStreamSerializerTests.cs` | 23 | Component | TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| PerspectiveCompletionFlushWorkerTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionFlushWorkerTests.cs` | 14 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerspectiveMigrationWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveMigrationWorkerBranchCoverageTests.cs` | 3 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| PerspectiveMigrationWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveMigrationWorkerCoverageTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerspectiveMigrationWorkerTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveMigrationWorkerTests.cs` | 10 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| PerspectiveWorkerAffinityHoldWatchdogTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerAffinityHoldWatchdogTests.cs` | 6 | Component | StartAsync, TimeoutWait, CrossThreadSignal, RealClock | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerChannelModeTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerChannelModeTests.cs` | 2 | Component |  | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerClaimedWorkSurvivesCooledDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerClaimedWorkSurvivesCooledDrainTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerCollectiveSinkTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.BranchCoverage.cs` | 2 | Component | StartAsync, TimeoutWait | partial PerspectiveWorkerCollectiveSinkTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerCollectiveSinkTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.Predecessor.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal | partial PerspectiveWorkerCollectiveSinkTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerCollectiveSinkTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.cs` | 34 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerCollectiveSinkTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCoverageTests.cs` | 42 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component); PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeadLetterFilterTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeadLetterFilterTests.cs` | 8 | Component |  | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDedupTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDedupTests.cs` | 9 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.BranchCoverage.cs` | 22 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.ConsumerWake.cs` | 4 | Component |  | partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.LifecycleFailurePaths.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal | EventIdSignalingLogger (Component); PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.StoredForm.cs` | 1 | Component | StartAsync, TimeoutWait | PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.TransientFailure.cs` | 2 | Component | StartAsync | EventIdSignalingLogger (Component); PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathChannelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.cs` | 13 | Component | StartAsync, TimeoutWait, CrossThreadSignal | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component); PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathChannelTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.BranchCoverage.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal | partial PerspectiveWorkerDeepPathDrainTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.RefetchExitPaths.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal | EventIdSignalingLogger (Component); partial PerspectiveWorkerDeepPathDrainTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.StoredForm.cs` | 3 | Component | StartAsync, TimeoutWait | partial PerspectiveWorkerDeepPathDrainTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.TransientFailure.cs` | 4 | Component | StartAsync, TimeoutWait | EventIdSignalingLogger (Component); partial PerspectiveWorkerDeepPathDrainTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathDrainTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.cs` | 21 | Component | StartAsync, TaskDelay, TaskRun, TimeoutWait, CrossThreadSignal, Stopwatch | PerspectiveWorkerTestHarness (Component); partial PerspectiveWorkerDeepPathDrainTests (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathMiscTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathMiscTests.cs` | 10 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDeepPathReplayTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathReplayTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerDrainModeLifecycleTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDrainModeLifecycleTests.cs` | 16 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| LifecycleCoordinatorPostAllPerspectivesIsolationTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDrainModeLifecycleTests.cs` | 1 | Component | TaskDelay |  | phase 2 waits for #1259 |
| PerspectiveWorkerDrainModeTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDrainModeTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerEventTypeProviderTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerEventTypeProviderTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerNoPerspectivesParkTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerNoPerspectivesParkTests.cs` | 1 | Component | StartAsync, TimeoutWait | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerParallelTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerParallelTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerRewindTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerRewindTests.cs` | 9 | Component | StartAsync, TaskDelay, TimeoutWait | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerSecurityContextTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerSecurityContextTests.cs` | 14 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerStartupAndMaintenanceTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStartupAndMaintenanceTests.cs` | 17 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerStartupGateTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStartupGateTests.cs` | 3 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerStrategyTests | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStrategyTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal | PerspectiveWorkerTestHarness (Component) | phase 2 waits for #1259 |
| PerspectiveWorkerTestHarness | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerTestHarness.cs` | 0 | Component |  | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| CapturingPerspectiveCompletionChannel | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerTestHarness.cs` | 0 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| CapturingFailureChannel | `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerTestHarness.cs` | 0 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| PrePublishGateForensicPreservationTests | `tests/Whizbang.Core.Tests/Workers/PrePublishGateForensicPreservationTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ProcessedEventCacheCoverageTests | `tests/Whizbang.Core.Tests/Workers/ProcessedEventCacheCoverageTests.cs` | 6 | Component | TaskRun |  | phase 2 waits for #1259 |
| PublishTimeoutTests | `tests/Whizbang.Core.Tests/Workers/PublishTimeoutTests.cs` | 3 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ReadModelsReadyDriverCoverageTests | `tests/Whizbang.Core.Tests/Workers/ReadModelsReadyDriverCoverageTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ReadModelsReadyDriverTests | `tests/Whizbang.Core.Tests/Workers/ReadModelsReadyDriverTests.cs` | 3 | Component | StartAsync, TaskDelay | CapturingFailureChannel (Component); CapturingPerspectiveCompletionChannel (Component) | phase 2 waits for #1259 |
| RecentlyProcessedEventCacheSweepWorkerTests | `tests/Whizbang.Core.Tests/Workers/RecentlyProcessedEventCacheSweepWorkerTests.cs` | 4 | Component | StartAsync, CrossThreadSignal |  | phase 2 waits for #1259 |
| RecoveryLifecycleHardeningTests | `tests/Whizbang.Core.Tests/Workers/RecoveryLifecycleHardeningTests.cs` | 6 | Component | StartAsync, TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| RepairDrainWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/RepairDrainWorkerCoverageTests.cs` | 5 | Component | StartAsync, TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| RepairDrainWorkerTests | `tests/Whizbang.Core.Tests/Workers/RepairDrainWorkerTests.cs` | 11 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SchemaGateShutdownCoverageTests | `tests/Whizbang.Core.Tests/Workers/SchemaGateShutdownCoverageTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal, RealClock |  | phase 2 waits for #1259 |
| SecurityContextTimeoutTests | `tests/Whizbang.Core.Tests/Workers/SecurityContextTimeoutTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ServiceBusConsumerSourceIdentityTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerSourceIdentityTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerBranchCoverageTests.cs` | 8 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerCoverageTests.cs` | 7 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerDeepCoverageTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerDeepCoverageTests.cs` | 31 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerDropGateTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerDropGateTests.cs` | 4 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerFlagDerivationTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerFlagDerivationTests.cs` | 5 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerGapTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerGapTests.cs` | 12 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| ServiceBusConsumerWorkerStartupCancellationTests | `tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerStartupCancellationTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowApplyBatchStrategyBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowApplyBatchStrategyBranchCoverageTests.cs` | 2 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowApplyBatchStrategyCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowApplyBatchStrategyCoverageTests.cs` | 5 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowApplyBatchStrategyTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowApplyBatchStrategyTests.cs` | 7 | Component | TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| SlidingWindowApplyFailurePathTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowApplyFailurePathTests.cs` | 3 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowBatcherCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowBatcherCoverageTests.cs` | 3 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| SlidingWindowBatcherTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowBatcherTests.cs` | 10 | Component | TaskDelay, TaskRun, TimeoutWait |  | phase 2 waits for #1259 |
| SlidingWindowInboxBatchStrategyBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyBranchCoverageTests.cs` | 1 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowInboxBatchStrategyCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyCoverageTests.cs` | 4 | Component | TaskDelay, TaskRun, TimeoutWait, CrossThreadSignal, RealClock |  | phase 2 waits for #1259 |
| SlidingWindowInboxBatchStrategyStopTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyStopTests.cs` | 1 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| SlidingWindowInboxBatchStrategyTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyTests.cs` | 15 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowOutboxBatchStrategyBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowOutboxBatchStrategyBranchCoverageTests.cs` | 1 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowOutboxBatchStrategyCoverageTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowOutboxBatchStrategyCoverageTests.cs` | 4 | Component | CrossThreadSignal |  | phase 2 waits for #1259 |
| SlidingWindowOutboxBatchStrategyTests | `tests/Whizbang.Core.Tests/Workers/SlidingWindowOutboxBatchStrategyTests.cs` | 16 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| SubscriptionExpansionWorkerTests | `tests/Whizbang.Core.Tests/Workers/SubscriptionExpansionWorkerTests.cs` | 6 | Component | StartAsync, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportBatchCollectorBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/TransportBatchCollectorBranchCoverageTests.cs` | 4 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportBatchCollectorTests | `tests/Whizbang.Core.Tests/Workers/TransportBatchCollectorTests.cs` | 8 | Component | TaskDelay, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerAdditionalCoverage2Tests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerAdditionalCoverage2Tests.cs` | 13 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerAdditionalCoverageTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerAdditionalCoverageTests.cs` | 16 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerBatchFailureTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBatchFailureTests.cs` | 1 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerBatchHandlerTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBatchHandlerTests.cs` | 6 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerBodyOffloadTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBodyOffloadTests.cs` | 7 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBranchCoverageTests.cs` | 9 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerBulkInsertInvariantTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBulkInsertInvariantTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerCompositeNoExpandTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerCompositeNoExpandTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerConnectionRecoveryTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerConnectionRecoveryTests.cs` | 8 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerControlClassReceiveTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerControlClassReceiveTests.cs` | 7 | Component | StartAsync, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerCoverageTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerCoverageTests.cs` | 34 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerDeepCoverageTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDeepCoverageTests.cs` | 20 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerDiWiringTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDiWiringTests.cs` | 2 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerDirectedTargetTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDirectedTargetTests.cs` | 5 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerDropGateTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDropGateTests.cs` | 4 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerFlagDerivationTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerFlagDerivationTests.cs` | 5 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerHealthMonitorInternalsTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerHealthMonitorInternalsTests.cs` | 1 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerKnownEventFilterTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerKnownEventFilterTests.NoConsumerGate.cs` | 1 | Component | StartAsync, TimeoutWait | partial TransportConsumerWorkerKnownEventFilterTests (Component) | phase 2 waits for #1259 |
| TransportConsumerWorkerKnownEventFilterTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerKnownEventFilterTests.cs` | 2 | Component | StartAsync, TimeoutWait | partial TransportConsumerWorkerKnownEventFilterTests (Component) | phase 2 waits for #1259 |
| TransportConsumerWorkerOwnedEventDiscardTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs` | 6 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerPoisonQuarantineTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerPoisonQuarantineTests.cs` | 7 | Component | StartAsync, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerProvisioningTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerProvisioningTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerResilienceEdgeTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerResilienceEdgeTests.cs` | 11 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerResilienceTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerResilienceTests.cs` | 13 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerSubscriptionsReadyTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerSubscriptionsReadyTests.cs` | 3 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportConsumerWorkerTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs` | 6 | Component | StartAsync | FakeTransport (Component); GatedReadinessCheck (Component) | phase 2 waits for #1259 |
| FakeTransport | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs` | 0 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| GatedReadinessCheck | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs` | 0 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerUncoveredPathsTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerUncoveredPathsTests.cs` | 17 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerUnstorableMessageTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerUnstorableMessageTests.cs` | 7 | Component | StartAsync, TimeoutWait |  | phase 2 waits for #1259 |
| TransportConsumerWorkerVerboseLoggingTests | `tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerVerboseLoggingTests.cs` | 2 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportDeadLetterDrainWorkerTests | `tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs` | 13 | Component | StartAsync, CrossThreadSignal |  | phase 2 waits for #1259 |
| TransportPublishStrategyThrottleRetryTests | `tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyThrottleRetryTests.cs` | 14 | Other | Stopwatch |  | phase 2 waits for #1259 |
| UngatedWorkerAdoptionTests | `tests/Whizbang.Core.Tests/Workers/UngatedWorkerAdoptionTests.cs` | 5 | Component | StartAsync, TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| WhizbangShutdownServiceCoverageTests | `tests/Whizbang.Core.Tests/Workers/WhizbangShutdownServiceCoverageTests.cs` | 1 | Component | StartAsync |  | phase 2 waits for #1259 |
| WorkCompletionMeterTests | `tests/Whizbang.Core.Tests/Workers/WorkCompletionMeterTests.cs` | 5 | Component | TaskRun |  | phase 2 waits for #1259 |
| WorkerLoopRecoveryTests | `tests/Whizbang.Core.Tests/Workers/WorkerLoopRecoveryTests.cs` | 7 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| WorkerPipelineExtensionsBranchCoverageTests | `tests/Whizbang.Core.Tests/Workers/WorkerPipelineExtensionsBranchCoverageTests.cs` | 1 | Component | TimeoutWait |  | phase 2 waits for #1259 |
| WorkerPipelineExtensionsCoverageTests | `tests/Whizbang.Core.Tests/Workers/WorkerPipelineExtensionsCoverageTests.cs` | 4 | Component | TimeoutWait, CrossThreadSignal |  | phase 2 waits for #1259 |
| WorkerThreadPoolFloorTests | `tests/Whizbang.Core.Tests/Workers/WorkerThreadPoolFloorTests.cs` | 5 | Component | ThreadPool |  | phase 2 waits for #1259 |

### Whizbang.Data.Schema.Tests

All 30 types are unit-pure.

### Whizbang.Data.Tests

All 17 types are unit-pure.

### Whizbang.Documentation.Tests

1 of 3 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| ConfigurationKeyManifestTests | `tests/Whizbang.Documentation.Tests/ConfigurationKeyManifestTests.cs` | 1 | Component | StartAsync, FileSystem |  |  |

### Whizbang.Execution.Tests

7 of 8 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| ExecutionStrategyContractTests | `tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs` | 10 | Component | StartAsync, TaskDelay |  |  |
| ParallelExecutorTests | `tests/Whizbang.Execution.Tests/ParallelExecutorTests.cs` | 13 | Component | StartAsync, TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| PooledSourcePoolTests | `tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs` | 13 | Component | TaskRun |  |  |
| PooledValueTaskSourceTests | `tests/Whizbang.Execution.Tests/PooledValueTaskSourceTests.cs` | 24 | Component | TaskRun, TimeoutWait, CrossThreadSignal |  |  |
| SerialExecutorDrainAfterStopTests | `tests/Whizbang.Execution.Tests/SerialExecutorDrainAfterStopTests.cs` | 1 | Component | StartAsync, CrossThreadSignal |  |  |
| SerialExecutorFaultingWorkItemTests | `tests/Whizbang.Execution.Tests/SerialExecutorFaultingWorkItemTests.cs` | 2 | Component | StartAsync, CrossThreadSignal |  |  |
| SerialExecutorTests | `tests/Whizbang.Execution.Tests/SerialExecutorTests.cs` | 20 | Component | StartAsync, TaskDelay |  |  |

### Whizbang.Generators.Tests

4 of 261 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| MessageRegistryDocsPathTests | `tests/Whizbang.Generators.Tests/MessageRegistryDocsPathTests.cs` | 2 | Other | FileSystem |  |  |
| MessageRegistryGeneratorCoverageTests | `tests/Whizbang.Generators.Tests/MessageRegistryGeneratorCoverageTests.cs` | 10 | Other | FileSystem |  |  |
| PathResolverTests | `tests/Whizbang.Generators.Tests/PathResolverTests.cs` | 6 | Other | FileSystem |  |  |
| ReceptorDiscoveryGeneratorTests | `tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs` | 81 | Other | FileSystem |  |  |

### Whizbang.Hosting.AspNet.Tests

10 of 29 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| ApplyStackEndpointsTests | `tests/Whizbang.Hosting.AspNet.Tests/ApplyStackEndpointsTests.cs` | 6 | Component | StartAsync, HostBuilder, TestServer |  |  |
| HostConfigurationDisposalTests | `tests/Whizbang.Hosting.AspNet.Tests/Configuration/HostConfigurationDisposalTests.cs` | 2 | Component | HostBuilder |  |  |
| DeadLetterOperatorEndpointsCoverageTests | `tests/Whizbang.Hosting.AspNet.Tests/DeadLetterOperatorEndpointsCoverageTests.cs` | 4 | Component | StartAsync, HostBuilder, TestServer |  |  |
| DeadLetterOperatorEndpointsTests | `tests/Whizbang.Hosting.AspNet.Tests/DeadLetterOperatorEndpointsTests.cs` | 12 | Component | StartAsync, HostBuilder, TestServer |  |  |
| StartupStatusEndpointsTests | `tests/Whizbang.Hosting.AspNet.Tests/StartupStatusEndpointsTests.cs` | 8 | Component | StartAsync, HostBuilder, TestServer |  |  |
| StreamRedeliveryEndpointsTests | `tests/Whizbang.Hosting.AspNet.Tests/StreamRedeliveryEndpointsTests.cs` | 6 | Component | StartAsync, HostBuilder, TestServer |  |  |
| WhizbangCorrelationPipelineTests | `tests/Whizbang.Hosting.AspNet.Tests/WhizbangCorrelationPipelineTests.cs` | 3 | Component | StartAsync, HostBuilder, TestServer |  |  |
| WhizbangFlushMiddlewareTests | `tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushMiddlewareTests.cs` | 5 | Component | StartAsync, HostBuilder, TestServer |  |  |
| WhizbangFlushStartupFilterTests | `tests/Whizbang.Hosting.AspNet.Tests/WhizbangFlushStartupFilterTests.cs` | 2 | Component | StartAsync, HostBuilder, TestServer |  |  |
| WhizbangSecurityHeadersStartupFilterTests | `tests/Whizbang.Hosting.AspNet.Tests/WhizbangSecurityHeadersStartupFilterTests.cs` | 4 | Component | StartAsync, HostBuilder, TestServer |  |  |

### Whizbang.Hosting.Azure.ServiceBus.Tests

3 of 7 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| ServiceBusReadinessCheckFailurePathTests | `tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckFailurePathTests.cs` | 10 | Component | TaskRun | GatedServiceBusClient (Component) |  |
| GatedServiceBusClient | `tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckFailurePathTests.cs` | 0 | Component | TimeoutWait |  |  |
| ServiceBusReadinessCheckTests | `tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs` | 5 | Component | TaskDelay |  |  |

### Whizbang.Hosting.RabbitMQ.Tests

All 1 types are unit-pure.

### Whizbang.LanguageServer.Tests

All 18 types are unit-pure.

### Whizbang.Migrate.Tests

23 of 62 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| MartenAnalyzerTests | `tests/Whizbang.Migrate.Tests/Analysis/MartenAnalyzerTests.cs` | 12 | Other | FileSystem |  |  |
| WolverineAnalyzerTests | `tests/Whizbang.Migrate.Tests/Analysis/WolverineAnalyzerTests.cs` | 43 | Other | FileSystem |  |  |
| AnalyzeCommandTests | `tests/Whizbang.Migrate.Tests/Commands/AnalyzeCommandTests.cs` | 6 | Other | FileSystem |  |  |
| ApplyCommandTests | `tests/Whizbang.Migrate.Tests/Commands/ApplyCommandTests.cs` | 12 | Other | FileSystem |  |  |
| ProgramCoverageTests | `tests/Whizbang.Migrate.Tests/Commands/ProgramCoverageTests.cs` | 3 | Other | FileSystem |  |  |
| RevertCommandCoverageTests | `tests/Whizbang.Migrate.Tests/Commands/RevertCommandCoverageTests.cs` | 4 | Other | FileSystem, Process |  |  |
| RevertCommandTests | `tests/Whizbang.Migrate.Tests/Commands/RevertCommandTests.cs` | 7 | Other | FileSystem |  |  |
| StatusCommandCoverageTests | `tests/Whizbang.Migrate.Tests/Commands/StatusCommandCoverageTests.cs` | 4 | Other | FileSystem |  |  |
| StatusCommandTests | `tests/Whizbang.Migrate.Tests/Commands/StatusCommandTests.cs` | 5 | Other | FileSystem |  |  |
| GitWorktreeServiceCoverageTests | `tests/Whizbang.Migrate.Tests/Git/GitWorktreeServiceCoverageTests.cs` | 2 | Other | FileSystem, Process |  |  |
| JsonMigrationJournalCoverageTests | `tests/Whizbang.Migrate.Tests/Journal/JsonMigrationJournalCoverageTests.cs` | 1 | Other | FileSystem |  |  |
| JsonMigrationJournalTests | `tests/Whizbang.Migrate.Tests/Journal/JsonMigrationJournalTests.cs` | 17 | Other | FileSystem |  |  |
| PackageManagerTests | `tests/Whizbang.Migrate.Tests/PackageManagement/PackageManagerTests.cs` | 18 | Other | FileSystem |  |  |
| ProgramCliTests | `tests/Whizbang.Migrate.Tests/ProgramCliTests.cs` | 36 | Other | FileSystem |  |  |
| MigrationProjectManagerCoverageTests | `tests/Whizbang.Migrate.Tests/Projects/MigrationProjectManagerCoverageTests.cs` | 5 | Other | FileSystem |  |  |
| MigrationProjectManagerTests | `tests/Whizbang.Migrate.Tests/Projects/MigrationProjectManagerTests.cs` | 10 | Other | FileSystem |  |  |
| DecisionFileTests | `tests/Whizbang.Migrate.Tests/Wizard/DecisionFileTests.cs` | 23 | Other | FileSystem |  |  |
| GitOperationsCoverageTests | `tests/Whizbang.Migrate.Tests/Wizard/GitOperationsCoverageTests.cs` | 2 | Other | FileSystem, Process |  |  |
| GitOperationsTests | `tests/Whizbang.Migrate.Tests/Wizard/GitOperationsTests.cs` | 13 | Other | FileSystem |  |  |
| MigrationStateDetectorCoverageTests | `tests/Whizbang.Migrate.Tests/Wizard/MigrationStateDetectorCoverageTests.cs` | 4 | Other | FileSystem |  |  |
| MigrationStateDetectorTests | `tests/Whizbang.Migrate.Tests/Wizard/MigrationStateDetectorTests.cs` | 5 | Other | FileSystem |  |  |
| WizardRunnerCoverageTests | `tests/Whizbang.Migrate.Tests/Wizard/WizardRunnerCoverageTests.cs` | 6 | Other | FileSystem |  |  |
| WizardRunnerTests | `tests/Whizbang.Migrate.Tests/Wizard/WizardRunnerTests.cs` | 13 | Other | FileSystem |  |  |

### Whizbang.Observability.Tests

3 of 24 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| EnvelopeRegistryTests | `tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs` | 11 | Component | Parallel |  |  |
| MessageTracingTests | `tests/Whizbang.Observability.Tests/MessageTracingTests.cs` | 63 | Component | TaskDelay |  |  |
| PolicyDecisionTrailTests | `tests/Whizbang.Observability.Tests/PolicyDecisionTrailTests.cs` | 8 | Component | TaskDelay |  |  |

### Whizbang.Offloads.AzureBlob.Tests

All 5 types are unit-pure.

### Whizbang.Offloads.InMemory.Tests

All 1 types are unit-pure.

### Whizbang.Partitioning.Tests

1 of 2 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| HashPartitionRouterTests | `tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs` | 14 | Component | TaskRun, Stopwatch |  |  |

### Whizbang.Policies.Tests

All 8 types are unit-pure.

### Whizbang.Sagas.Tests

4 of 48 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| SagaWatchdogTickDeliveryIntegrationTests | `tests/Whizbang.Sagas.Tests/SagaWatchdogTickDeliveryIntegrationTests.cs` | 6 | Component | StartAsync |  |  |
| SagaWatchdogTickSubscriptionIntegrationTests | `tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs` | 5 | Component | StartAsync |  |  |
| SagaClaimPruneStepTests | `tests/Whizbang.Sagas.Tests/Services/SagaClaimPruneStepTests.cs` | 10 | Other | RealClock |  |  |
| SagaWatchdogTickRoutingTests | `tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs` | 8 | Component | StartAsync |  |  |

### Whizbang.Sequencing.Tests

1 of 1 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| InMemorySequenceProviderTests | `tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs` | 12 | Component | TaskRun, Parallel, Stopwatch |  |  |

### Whizbang.SignalR.Tests

All 6 types are unit-pure.

### Whizbang.Testing.Tests

10 of 34 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| CountingPerspectiveReceptorTests | `tests/Whizbang.Testing.Tests/Lifecycle/CountingPerspectiveReceptorTests.cs` | 6 | Component | CrossThreadSignal |  |  |
| LifecycleStageAwaiterTests | `tests/Whizbang.Testing.Tests/Lifecycle/LifecycleStageAwaiterTests.cs` | 23 | Component | TimeoutWait |  |  |
| MultiHostPerspectiveAwaiterTests | `tests/Whizbang.Testing.Tests/Lifecycle/MultiHostPerspectiveAwaiterTests.cs` | 13 | Component | TimeoutWait |  |  |
| InMemoryWireTransportTests | `tests/Whizbang.Testing.Tests/MultiService/InMemoryWireTransportTests.cs` | 4 | Component | CrossThreadSignal |  |  |
| MultiServiceHarnessTests | `tests/Whizbang.Testing.Tests/MultiService/MultiServiceHarnessTests.cs` | 4 | Component | StartAsync |  |  |
| TraceAssertionExtensionsTests | `tests/Whizbang.Testing.Tests/Observability/TraceAssertionExtensionsTests.cs` | 22 | Other | FileSystem |  |  |
| QueryPlanCaptureTests | `tests/Whizbang.Testing.Tests/QueryPlanCaptureTests.cs` | 3 | Integration | Container, NetworkClient |  |  |
| MessageAwaiterTests | `tests/Whizbang.Testing.Tests/Transport/MessageAwaiterTests.cs` | 19 | Component | TimeoutWait |  |  |
| SubscriptionWarmupTests | `tests/Whizbang.Testing.Tests/Transport/SubscriptionWarmupTests.cs` | 13 | Component | TimeoutWait |  |  |
| PerspectiveWorkerTestHarnessTests | `tests/Whizbang.Testing.Tests/Workers/PerspectiveWorkerTestHarnessTests.cs` | 10 | Component | TimeoutWait |  |  |

### Whizbang.Transports.AzureServiceBus.Tests

10 of 96 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| AsbAcceptorAdaptiveWiringTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/AsbAcceptorAdaptiveWiringTests.cs` | 9 | Component | CrossThreadSignal |  |  |
| AzureServiceBusErrorHandlingTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusErrorHandlingTests.cs` | 41 | Component | TaskDelay |  |  |
| AzureServiceBusTransportBatchPipelineTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportBatchPipelineTests.cs` | 12 | Component | CrossThreadSignal |  |  |
| AzureServiceBusTransportThrottleAndAdaptiveTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportThrottleAndAdaptiveTests.cs` | 11 | Component | TimeoutWait, CrossThreadSignal |  |  |
| AzureServiceBusTransportUnitTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs` | 31 | Component | TimeoutWait |  |  |
| AsbFinalPassBranchTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/BranchCoverage/AsbFinalPassBranchTests.cs` | 6 | Component | CrossThreadSignal |  |  |
| ReceiveLivenessWatchdogCoverageTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/ReceiveLivenessWatchdogCoverageTests.cs` | 2 | Component | TimeoutWait, CrossThreadSignal |  |  |
| ReceiveLivenessWatchdogTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/ReceiveLivenessWatchdogTests.cs` | 13 | Component | CrossThreadSignal |  |  |
| ServiceBusReadinessCheckCoverageTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusReadinessCheckCoverageTests.cs` | 4 | Component | TaskRun, TimeoutWait |  |  |
| ServiceBusReadinessCheckTests | `tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusReadinessCheckTests.cs` | 5 | Component | TaskDelay |  |  |

### Whizbang.Transports.FastEndpoints.Tests

All 21 types are unit-pure.

### Whizbang.Transports.HotChocolate.Tests

All 46 types are unit-pure.

### Whizbang.Transports.Mutations.Tests

All 13 types are unit-pure.

### Whizbang.Transports.RabbitMQ.Tests

1 of 29 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| RabbitMQConnectionRetryTests | `tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQConnectionRetryTests.cs` | 25 | Other | NetworkClient |  |  |

### Whizbang.Transports.Tests

8 of 24 types are not unit-pure.

| Class | File | Tests | Category | Constructs | Via | Note |
|---|---|---:|---|---|---|---|
| DispatcherTransportBridgePriorityTests | `tests/Whizbang.Transports.Tests/DispatcherTransportBridgePriorityTests.cs` | 3 | Component | TimeoutWait, CrossThreadSignal |  |  |
| DispatcherTransportBridgeTests | `tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs` | 8 | Component | TimeoutWait, CrossThreadSignal |  |  |
| ITransportTests | `tests/Whizbang.Transports.Tests/ITransportTests.cs` | 8 | Component | TimeoutWait, CrossThreadSignal |  |  |
| InProcessTransportTests | `tests/Whizbang.Transports.Tests/InProcessTransportTests.cs` | 21 | Component | TaskDelay, TimeoutWait, CrossThreadSignal |  |  |
| SubscribeBatchTests | `tests/Whizbang.Transports.Tests/SubscribeBatchTests.cs` | 10 | Component | TaskDelay, TimeoutWait |  |  |
| TransportManagerPriorityTests | `tests/Whizbang.Transports.Tests/TransportManagerPriorityTests.cs` | 3 | Component | TimeoutWait, CrossThreadSignal |  |  |
| TransportManagerPublishingTests | `tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs` | 7 | Component | TimeoutWait, CrossThreadSignal |  |  |
| TransportManagerSubscriptionTests | `tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs` | 12 | Component | TimeoutWait |  |  |
