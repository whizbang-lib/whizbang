# Test Projects Overview

Every test project declares its **type** in `<WhizbangTestType>` and its suite **tags** in
`<WhizbangTestTags>`. The type decides which runs select the project; the tags decide which
integration suite runs it. Both are load-bearing: a project whose type or tag no tool knows builds
fine and then never runs, with every check green. #1196 found 214 integration tests that had never run
in CI that way, and `Whizbang.LanguageServer.Tests` ran nowhere until #1264 because it declared no type.

## Test types

| Type | What a test in it may do | Naming | `Run-Tests.ps1` mode | CI suite |
|------|--------------------------|--------|----------------------|----------|
| **Unit** | One deterministic flow: an injected fake clock, no background threads or hosted workers, no real I/O. Nothing in it can race, so it cannot be timing-dependent. | `<Source>.Tests` | `Unit`, `AiUnit` | Test · Unit |
| **Component** | Real workers and threads in one process, no external infrastructure. The test fixes every interleaving through signals, and every asynchronous assertion waits on the signal for the exact transition it asserts: never a delay, a poll, a timeout used as a wait, or something that merely correlates (`StopAsync` returning, a completion report). | `<Source>.Component.Tests` | `Component`, `AiComponent` | Test · Component |
| **Integration** | Containers and real infrastructure. Each project carries the tag of the suite that runs it. | `<Source>.Integration.Tests` | `Integration`, `AiIntegrations` (with `-Tag`) | one per tag |
| **Benchmark** | BenchmarkDotNet measurements. | `Whizbang.Benchmarks*` | never | none (on demand) |
| **Soak** | Load, stress and soak measurements; wall-clock properties. | `Whizbang.Soak.Tests` | never | none (`scripts/Run-Soak.ps1`) |

`-Mode All` and `-Mode Ai` run Unit, Component and Integration projects.

### Classifying a test

Take the first rule that matches (#1267). The types follow Michael Feathers' unit-test rules and
Google's small/medium/large test sizes; the `testing` skill (`.claude/skills/testing/SKILL.md`) has
the reasoning.

1. Containers, a database, a broker, the network or real cloud infrastructure: **Integration**.
2. Starts a child process: **Integration** (it crosses a process boundary).
3. Starts a hosted worker, `Task.Run` or a `Thread`: **Component**.
4. Reads or writes the real file system: **Component**.
5. Sleeps or reads the real clock, otherwise one flow: **stays Unit**, refactored onto an injected
   `TimeProvider` driven by `FakeTimeProvider`. Moving it would keep the race; only a class that
   cannot take a fake clock moves to Component, with the reason in its pull request.
6. Otherwise: **Unit**.

Tags follow the type: `Unit` for unit projects, `Component` for component projects, and for
integration projects the suite tag (`Postgres`, `RabbitMQ`, `AzureServiceBus`, `AzureBlob`,
`InMemory`, or the plain `Integration` for in-process hosts) plus `Docker` when it starts containers.

### Unit projects still hold non-unit tests

The Unit projects predate the Component type and still hold tests that start real workers, use the
real clock, or do real I/O. #1264 moves them, one source project per pull request:

- the worklist is `plans/test-separation-inventory.md` (with `plans/test-separation-inventory.csv`):
  every test class in every Unit project, classified with the construct that makes it non-unit;
- the purity guard (`.github/scripts/Get-TestPurity.ps1`, run by "Test · Pipeline scripts") lists
  those classes per project. It reports only, and is switched to failing once the moves are done.

## Running tests

```bash
pwsh scripts/Run-Tests.ps1 -Mode AiUnit          # unit tests only (fast)
pwsh scripts/Run-Tests.ps1 -Mode AiComponent     # component tests only
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations  # integration tests only (Docker)
pwsh scripts/Run-Tests.ps1 -Mode Ai              # Unit, Component and Integration
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core" # projects whose name matches
pwsh scripts/Run-Tests.ps1 -FailFast             # stop on the first failure
```

### CI suites

Every suite runs `Run-Tests.ps1` exactly as below (with `-NoBuild` on the build job's output), so a
local run reproduces it.

| Workflow | Selection | Build slice |
|----------|-----------|-------------|
| `reusable-test-unit.yml` | `-Mode Unit` | full build |
| `reusable-test-component.yml` | `-Mode Component` | every Component project |
| `reusable-test-postgres.yml` | `-Mode Integration -Tag Postgres` (sharded) | Postgres projects |
| `reusable-test-inmemory.yml` | `-Mode Integration -Tag InMemory` | InMemory projects |
| `reusable-test-rabbitmq.yml` | `-Mode Integration -Tag RabbitMQ` | RabbitMQ projects |
| `reusable-test-servicebus.yml` | `-Mode Integration -Tag AzureServiceBus` (sharded) | Service Bus projects and the ECommerce samples |
| `reusable-test-azureblob.yml` | `-Mode Integration -Tag AzureBlob` | Azure Blob projects |
| `reusable-test-integration.yml` | `-Mode Integration -Tag Integration` | in-process integration projects |

`ai-docs/test-sharding.md` covers slices and shards.

## Adding a test type

A new type must be taught to **every** reader of `<WhizbangTestType>` in the same pull request, or its
projects are silently skipped:

1. `scripts/Run-Tests.ps1`: an entry in `$WhizbangTestTypes` (its modes, whether `-Mode All` runs it,
   or what runs it instead), and each new mode in the `-Mode` and `-LogMode` `ValidateSet`.
2. `Directory.Build.targets`: `<WhizbangKnownTestTypes>`. A project declaring any other type, or a
   test project declaring none, fails its build (WHZ0002, WHZ0003).
3. `scripts/Test-SolutionTestProjects.ps1`: `$KnownTestTypes`.
4. `.github/scripts/Get-TestSlice.ps1`: a suite slice of that type, or an entry in `$UnslicedTypes`
   with the reason (plus the slice upload in `reusable-build.yml`).
5. CI: a `reusable-test-*.yml` suite running one of its modes, a job for it in `ci.yml`, and that job
   in `.github/scripts/Test-CiResult.ps1`'s `$script:Suites`, in the needs of `ci-result`,
   `test-results`, `publish-test-status` and `notify-cancelled`, and in the needs and success
   conditions of `prerelease-publish` and `release-publish`.
6. `scripts/Run-PR.ps1`: a step running it, when `-Mode All` runs it.
7. This document, `ai-docs/testing-tunit.md`, `ai-docs/testing-async-patterns.md` and
   `ai-docs/test-sharding.md`.

`.github/scripts/Test-WhizbangTestType.ps1` (run by "Test · Pipeline scripts") checks items 1 to 6:
it fails naming each type and each reader that does not know it, and each suite list that misses a
suite. The docs site also reads test projects: its test status is published per project, so check its
scripts handle the new project too.

## Moving a test between projects

Code and docs point at tests by path: about 6,000 `<tests>` tags in `src/`, plus ai-docs, READMEs,
plans and the docs site. Move tests only with `scripts/Move-TestReference.ps1`, which rewrites every
reference in the library and reports (or rewrites) the docs-site references; run it with `-WhatIf`
first. The link guard (`.github/scripts/Test-TestsTagLink.ps1`, run by "Test · Pipeline scripts")
fails on any `<tests>` tag naming a file that does not exist or a method the file does not declare,
so a missed relink cannot merge. Its baseline (`.github/scripts/tests-tag-link-baseline.txt`) lists
the tags that were already broken, each with a reason.

## Projects

### Unit (34)

`tests/`: `Whizbang.CLI.Tests`, `Whizbang.Core.Tests`, `Whizbang.Data.Schema.Tests`,
`Whizbang.Data.Tests`, `Whizbang.Documentation.Tests`, `Whizbang.Execution.Tests`,
`Whizbang.Generators.Tests`, `Whizbang.Hosting.AspNet.Tests`, `Whizbang.Hosting.Azure.ServiceBus.Tests`,
`Whizbang.Hosting.RabbitMQ.Tests`, `Whizbang.LanguageServer.Tests`, `Whizbang.Migrate.Tests`,
`Whizbang.Observability.Tests`, `Whizbang.Offloads.AzureBlob.Tests`, `Whizbang.Offloads.InMemory.Tests`,
`Whizbang.Partitioning.Tests`, `Whizbang.Policies.Tests`, `Whizbang.Sagas.Tests`,
`Whizbang.Sequencing.Tests`, `Whizbang.SignalR.Tests`, `Whizbang.Testing.Tests`,
`Whizbang.Transports.AzureServiceBus.Tests`, `Whizbang.Transports.FastEndpoints.Tests`,
`Whizbang.Transports.HotChocolate.Tests`, `Whizbang.Transports.Mutations.Tests`,
`Whizbang.Transports.RabbitMQ.Tests`, `Whizbang.Transports.Tests`.

`samples/ECommerce/`: `ECommerce.Contracts.Tests`, `ECommerce.IntegrationTests` (in-memory checks;
despite the name, a unit project), `ECommerce.BFF.API.Tests`, `ECommerce.InventoryWorker.Tests`,
`ECommerce.NotificationWorker.Tests`, `ECommerce.OrderService.Tests`, `ECommerce.PaymentWorker.Tests`,
`ECommerce.ShippingWorker.Tests`. `ECommerce.BFF.API.Tests` and `ECommerce.InventoryWorker.Tests` carry
`Integration;Docker` tags while declaring Unit; the inventory classifies their classes.

### Component (3)

| Project | Purpose |
|---------|---------|
| `Whizbang.Core.Component.Tests` | Component tests of `Whizbang.Core`; receives the component tests moved out of `Whizbang.Core.Tests` |
| `Whizbang.Transports.Component.Tests` | Component tests of the transport abstractions (in-process transport, transport manager and dispatcher bridge on real subscriptions and threads), moved out of `Whizbang.Transports.Tests` |
| `Whizbang.Transports.RabbitMQ.Component.Tests` | Component tests of the RabbitMQ transport (real consumer threads, flush loops and drainers against in-process channel doubles), moved out of `Whizbang.Transports.RabbitMQ.Tests` |

### Integration (13)

| Project | Tags | Suite |
|---------|------|-------|
| `Whizbang.Data.EFCore.Postgres.Tests` | Postgres;Docker;Data | PostgreSQL (five shards) |
| `Whizbang.Data.Dapper.Postgres.Tests` | Postgres;Docker;Data | PostgreSQL |
| `Whizbang.Transports.RabbitMQ.Integration.Tests` | RabbitMQ;Docker;Messaging | RabbitMQ |
| `ECommerce.RabbitMQ.Integration.Tests` | RabbitMQ;Docker;Messaging;ECommerce | RabbitMQ |
| `ECommerce.Lifecycle.Integration.Tests` | RabbitMQ;Docker;Lifecycle | RabbitMQ |
| `Whizbang.Transports.AzureServiceBus.Integration.Tests` | AzureServiceBus;Docker;Messaging | Service Bus |
| `ECommerce.AzureServiceBus.Integration.Tests` | AzureServiceBus;Docker;Messaging;ECommerce | Service Bus |
| `ECommerce.InMemory.Integration.Tests` | Docker;ECommerce;InMemory | InMemory |
| `Whizbang.Offloads.AzureBlob.Integration.Tests` | AzureBlob;Docker;Offloads | Azure Blob |
| `Whizbang.Core.Integration.Tests` | Integration | Integration (general) |
| `Whizbang.Migrate.Integration.Tests` | Integration | Integration (general) |
| `Whizbang.Transports.FastEndpoints.Integration.Tests` | Integration | Integration (general) |
| `Whizbang.Transports.HotChocolate.Integration.Tests` | Integration | Integration (general) |

Every integration project must carry a tag some suite selects: `.github/scripts/tests/Get-TestSlice.Tests.ps1`
fails otherwise.

### Benchmark (2) and Soak (1)

`benchmarks/Whizbang.Benchmarks` and `benchmarks/Whizbang.Benchmarks.Postgres` (BenchmarkDotNet:
`dotnet run -c Release` in the project), and `tests/Whizbang.Soak.Tests` (`pwsh scripts/Run-Soak.ps1`).
None is part of the pull request gate. `scripts/Test-SolutionTestProjects.ps1` lists the two kept
outside `Whizbang.slnx`, with the reason.

## Coverage configuration

Every test project references `Microsoft.Testing.Extensions.CodeCoverage`; each CI suite collects line
and branch coverage per test module (`Run-Tests.ps1 -ModuleCoverage`) and the quality job merges every
suite's `coverage-*` artifact. Settings: `codecoverage.config` and `codecoverage.runsettings`.
