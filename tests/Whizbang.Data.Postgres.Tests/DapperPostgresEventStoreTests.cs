using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresEventStore using PostgreSQL.
/// Inherits all contract tests from EventStoreContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresEventStoreTests : EventStoreContractTests, IDisposable {
  private PostgresTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_testBase != null) {
      await _testBase.DisposeAsync();
    }
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  protected override Task<IEventStore> CreateEventStoreAsync() {
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var sizeValidator = new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance);
    var policyEngine = new PolicyEngine();
    var logger = NullLogger<DapperPostgresEventStore>.Instance;

    var eventStore = new DapperPostgresEventStore(
      _testBase.ConnectionFactory,
      _testBase.Executor,
      jsonOptions,
      adapter,
      sizeValidator,
      policyEngine,
      null, // perspectiveInvoker
      logger
    );
    return Task.FromResult<IEventStore>(eventStore);
  }

  private sealed class TestFixture : PostgresTestBase { }
}
