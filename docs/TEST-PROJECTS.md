# Test Projects Overview

This document categorizes all test-related projects in the Whizbang solution.

## Running Tests

### Local Development

Use `Run-Tests.ps1` for all test execution:

```bash
# Run unit tests (excludes integration/Postgres tests)
pwsh scripts/Run-Tests.ps1

# Run with coverage collection
pwsh scripts/Run-Tests.ps1 -Coverage

# Run specific project
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core"

# Run unit tests only (fast, ~5800 tests)
pwsh scripts/Run-Tests.ps1 -Mode AiUnit

# Run only integration tests
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations

# AI-optimized output, ALL tests (default)
pwsh scripts/Run-Tests.ps1 -Mode Ai

# Stop on first failure
pwsh scripts/Run-Tests.ps1 -FailFast
```

### CI Workflows

All CI workflows use `Run-Tests.ps1` for consistency with local development. Run these locally to verify before pushing:

```bash
# Unit tests (reusable-test-unit.yml) - 25 unit test projects
pwsh scripts/Run-Tests.ps1 -Mode Unit -Coverage -Configuration Release

# PostgreSQL tests (reusable-test-postgres.yml) - requires Docker
pwsh scripts/Run-Tests.ps1 -Mode Integration -Coverage -Configuration Release -Tag Postgres

# InMemory integration (reusable-test-inmemory.yml) - requires Docker
pwsh scripts/Run-Tests.ps1 -Mode Integration -Coverage -Configuration Release -Tag InMemory

# RabbitMQ integration (reusable-test-rabbitmq.yml) - requires Docker
pwsh scripts/Run-Tests.ps1 -Mode Integration -Coverage -Configuration Release -Tag RabbitMQ

# ServiceBus integration (reusable-test-servicebus.yml) - requires Docker
pwsh scripts/Run-Tests.ps1 -Mode Integration -Coverage -Configuration Release -Tag AzureServiceBus
```

| Workflow | Tests | Timeout |
|----------|-------|---------|
| `reusable-test-unit.yml` | 19 unit test projects (dynamic discovery) | 30 min |
| `reusable-test-postgres.yml` | 2 Postgres projects | 45 min |
| `reusable-test-inmemory.yml` | 1 InMemory integration | 20 min |
| `reusable-test-rabbitmq.yml` | 1 RabbitMQ integration | 45 min |
| `reusable-test-servicebus.yml` | 1 ServiceBus integration | 45 min |

---

## Project Categories

| Category | Purpose | Coverage | CI Workflow |
|----------|---------|----------|-------------|
| **Unit Tests** | Fast, isolated tests with mocks | Yes | `reusable-test-unit.yml` |
| **Integration Tests** | Full system tests with Testcontainers | Yes | Various per transport |
| **Benchmarks** | Performance measurement with BenchmarkDotNet | No | Manual |

---

## Unit Tests (tests/ directory)

Located in `tests/` directory. Fast tests that use mocks (Rocks) and don't require external dependencies.

### Library Unit Tests (19 projects)

| Project | Tests |
|---------|-------|
| `Whizbang.Core.Tests` | Core dispatcher, messaging, observability |
| `Whizbang.Data.Schema.Tests` | Schema validation |
| `Whizbang.Data.Tests` | Data layer abstractions |
| `Whizbang.Documentation.Tests` | Documentation validation |
| `Whizbang.Execution.Tests` | Execution pipeline, work distribution |
| `Whizbang.Generators.Tests` | Source generator output validation |
| `Whizbang.Hosting.Azure.ServiceBus.Tests` | Azure Service Bus hosting |
| `Whizbang.Hosting.RabbitMQ.Tests` | RabbitMQ hosting |
| `Whizbang.Migrate.Tests` | Migration tooling |
| `Whizbang.Observability.Tests` | Tracing, metrics, logging |
| `Whizbang.Partitioning.Tests` | Partition strategies |
| `Whizbang.Policies.Tests` | Retry, circuit breaker policies |
| `Whizbang.Sequencing.Tests` | Message sequencing |
| `Whizbang.SignalR.Tests` | SignalR integration |
| `Whizbang.Transports.FastEndpoints.Tests` | FastEndpoints integration |
| `Whizbang.Transports.HotChocolate.Tests` | GraphQL/HotChocolate integration |
| `Whizbang.Transports.Mutations.Tests` | Mutation handling |
| `Whizbang.Transports.RabbitMQ.Tests` | RabbitMQ transport (mocked) |
| `Whizbang.Transports.Tests` | Transport abstractions |

### PostgreSQL Integration Tests (2 projects)

Use Testcontainers for real PostgreSQL. Run in `reusable-test-postgres.yml`.

| Project | Tests |
|---------|-------|
| `Whizbang.Data.Postgres.Tests` | Dapper-based PostgreSQL operations |
| `Whizbang.Data.EFCore.Postgres.Tests` | EF Core PostgreSQL operations |

---

## Sample Tests (samples/ECommerce/)

Located in `samples/ECommerce/`. Test the ECommerce sample application.

### Sample Unit Tests (7 projects)

Fast tests for individual sample components.

| Project | Tests |
|---------|-------|
| `ECommerce.BFF.API.Tests` | BFF API endpoints |
| `ECommerce.Contracts.Tests` | Contract/message validation |
| `ECommerce.InventoryWorker.Tests` | Inventory worker logic |
| `ECommerce.NotificationWorker.Tests` | Notification worker logic |
| `ECommerce.OrderService.Tests` | Order service logic |
| `ECommerce.PaymentWorker.Tests` | Payment worker logic |
| `ECommerce.ShippingWorker.Tests` | Shipping worker logic |

### Sample Integration Tests (4 projects)

Full system tests using Testcontainers (PostgreSQL, RabbitMQ, or Azure Service Bus emulator).

| Project | Transport | CI Workflow |
|---------|-----------|-------------|
| `ECommerce.InMemory.Integration.Tests` | In-memory (no transport) | `reusable-test-inmemory.yml` |
| `ECommerce.RabbitMQ.Integration.Tests` | RabbitMQ | `reusable-test-rabbitmq.yml` |
| `ECommerce.Integration.Tests` | Azure Service Bus | `reusable-test-servicebus.yml` |
| `ECommerce.IntegrationTests` | Aspire-based integration | (manual) |

---

## Benchmarks (1 project)

Located in `benchmarks/`. Performance testing with BenchmarkDotNet.

| Project | Purpose |
|---------|---------|
| `Whizbang.Benchmarks` | Dispatcher, serialization, routing performance |

**Run benchmarks:**
```bash
cd benchmarks/Whizbang.Benchmarks
dotnet run -c Release
```

---

## Coverage Configuration

All test projects (32 total) have explicit `Microsoft.Testing.Extensions.CodeCoverage` for consistent coverage collection.

**Coverage settings:** `codecoverage.runsettings`
- `ExcludeAssembliesWithoutSources=None` - Ensures coverage works in CI with artifacts
- Includes: `Whizbang.*`, `ECommerce.*` assemblies
- Excludes: Test assemblies, Testcontainers, TUnit, Rocks, Bogus

---

## Summary

| Category | Count |
|----------|-------|
| Unit Tests (tests/) | 19 |
| PostgreSQL Integration (tests/) | 2 |
| Sample Unit Tests | 7 |
| Sample Integration Tests | 4 |
| **Total Test Projects** | **32** |
| Benchmark Projects | 1 |
