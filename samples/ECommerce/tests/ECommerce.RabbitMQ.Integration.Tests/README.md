# RabbitMQ Integration Tests

This test project contains end-to-end integration tests for the ECommerce sample application using RabbitMQ as the message transport.

## Architecture

- **Per-Class TestContainers**: Each test class gets isolated RabbitMQ (v3.13-management-alpine) and PostgreSQL (v17-alpine) containers
- **Parallel Execution**: Test classes run concurrently (default: 8 concurrent classes based on CPU cores)
- **Test Isolation**: Test-specific exchange/queue names prevent interference between tests
- **No Database Cleanup**: Each test gets fresh databases via per-class containers

## Test Coverage

### Lifecycle Tests (48 tests across 5 classes)

1. **OutboxLifecycleTests** (10 tests)
   - PreOutboxInline, PreOutboxAsync, PostOutboxAsync, PostOutboxInline stages
   - Tests hooks around ProcessWorkBatchAsync() for event publishing

2. **InboxLifecycleTests** (11 tests)
   - PreInboxInline, PreInboxAsync, PostInboxAsync, PostInboxInline stages
   - Tests hooks around message receipt and receptor invocation

3. **PerspectiveLifecycleTests** (13 tests)
   - PrePerspectiveInline, PrePerspectiveAsync, PostPerspectiveAsync, PostPerspectiveInline stages
   - Tests hooks around perspective event processing

4. **DistributeLifecycleTests** (9 tests)
   - PreDistributeInline, PreDistributeAsync, MidDistribute, PostDistributeAsync, PostDistributeInline stages
   - Tests hooks around outbox event distribution to transport

5. **ImmediateAsyncLifecycleTests** (5 tests)
   - ImmediateAsync stage (fires immediately after command handler, before DB operations)
   - Tests synchronous command processing hooks

### Workflow Tests (15 tests across 3 classes)

1. **CreateProductWorkflowTests** (4 tests)
   - End-to-end product creation workflow
   - Tests: basic creation, multiple products, zero stock, null image URL

2. **UpdateProductWorkflowTests** (6 tests)
   - End-to-end product update workflow
   - Tests: name only, all fields, price only, description+image, sequential updates, inventory isolation

3. **RestockInventoryWorkflowTests** (5 tests)
   - End-to-end inventory restocking workflow
   - Tests: basic restock, multiple restocks, from zero, zero quantity, large quantities

## Running Tests

### Prerequisites

- Docker installed and running (for TestContainers)
- .NET 10 SDK
- 16GB RAM recommended (for parallel test execution with containers)

### Run All RabbitMQ Integration Tests

```bash
# From repository root
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -Mode IntegrationsOnly

# Or directly via dotnet test
dotnet test samples/ECommerce/tests/ECommerce.RabbitMQ.Integration.Tests/
```

### Run Specific Test Categories

```bash
# Run only lifecycle tests
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -TestFilter "/*/*/*LifecycleTests/*"

# Run only workflow tests
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -TestFilter "/*/*/*WorkflowTests/*"

# Run specific test class
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -TestFilter "/*/ECommerce.RabbitMQ.Integration.Tests.Lifecycle/OutboxLifecycleTests/*"
```

### Run Both Azure Service Bus and RabbitMQ Tests

```bash
# Run all integration tests (both transports)
pwsh scripts/Run-Tests.ps1 -Mode IntegrationsOnly

# Or with AI-optimized output
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations
```

## Parallelization Settings

The RabbitMQ integration tests leverage TUnit's per-class fixtures with TestContainers for optimal parallelization:

- **Default Parallelism**: `Environment.ProcessorCount` (typically 8-16 concurrent test classes)
- **Container Startup**: ~15-20 seconds per test class (RabbitMQ + PostgreSQL)
- **Test Execution**: ~30-45 seconds per test
- **Total Time**: ~45-60 seconds for full suite (vs ~150 seconds sequential for Azure Service Bus)

### Adjust Parallelism

```bash
# Run with 4 concurrent test classes (for resource-constrained environments)
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -Mode IntegrationsOnly -MaxParallel 4

# Run with maximum parallelism (all CPU cores)
pwsh scripts/Run-Tests.ps1 -ProjectFilter "RabbitMQ.Integration.Tests" -Mode IntegrationsOnly -MaxParallel 0
```

## Performance Comparison

| Transport | Execution Mode | Test Count | Time |
|-----------|----------------|------------|------|
| Azure Service Bus | Sequential (NotInParallel) | 63 tests | ~150s |
| RabbitMQ | Parallel (8 concurrent) | 63 tests | ~45-60s |

**Performance Gain**: 2.5-3.3x faster with RabbitMQ parallel execution

## Key Differences from Azure Service Bus Tests

1. **No Shared Fixture**: Each test class gets its own containers (no SharedFixtureSource)
2. **No Message Draining**: Management API deletes test-specific queues/exchanges on cleanup
3. **Simplified Cleanup**: No database cleanup needed (fresh DBs per test class)
4. **Primary Constructor**: Uses C# 12 primary constructor pattern for fixtures
5. **Parallel Execution**: No `[NotInParallel]` attribute - full parallelism enabled

## Test Fixture Pattern

```csharp
[Category("Integration")]
[Category("Lifecycle")]
[ClassDataSource<RabbitMqClassFixtureSource>(Shared = SharedType.PerClass)]
public class OutboxLifecycleTests(RabbitMqClassFixtureSource fixtureSource) {
  private RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    await fixtureSource.InitializeAsync(); // Start containers
    _fixture = new RabbitMqIntegrationFixture(
      fixtureSource.RabbitMqConnectionString,
      fixtureSource.PostgresConnectionString,
      fixtureSource.ManagementApiUri,
      testClassName: nameof(OutboxLifecycleTests)
    );
    await _fixture.InitializeAsync(); // Create hosts
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync(); // Stop hosts, containers cleaned up automatically
      _fixture = null;
    }
  }
}
```

## Troubleshooting

### Tests Timeout

- Increase timeout: `[Timeout(90000)]` (90 seconds)
- Reduce parallelism: `-MaxParallel 4`
- Check Docker resource limits (CPU, memory)

### Container Startup Failures

- Ensure Docker is running
- Check available disk space
- Verify no port conflicts (5432, 5672, 15672)

### RabbitMQ Management API Issues

- Management plugin is enabled by default in rabbitmq:3.13-management-alpine
- Default credentials: guest/guest
- Management UI: http://localhost:{mapped-port} (check container logs for port)

## CI/CD Integration

The RabbitMQ integration tests are designed for CI/CD environments:

```bash
# GitHub Actions, Azure Pipelines, etc.
dotnet test samples/ECommerce/tests/ECommerce.RabbitMQ.Integration.Tests/ --max-parallel-test-modules 4 --logger "trx;LogFileName=rabbitmq-tests.trx"
```

**Recommended CI Settings**:
- Max Parallel: 4 (for most CI runners)
- Timeout: 10 minutes per test class
- RAM: 8GB minimum, 16GB recommended

## Related Documentation

- [TUnit Testing Framework](https://tunit.dev)
- [TestContainers for .NET](https://testcontainers.com/modules/dotnet/)
- [RabbitMQ Docker Image](https://hub.docker.com/_/rabbitmq)
- [PostgreSQL Docker Image](https://hub.docker.com/_/postgres)
