using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresEventStore using PostgreSQL.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[NotInParallel]
[InheritsTests]
public class DapperPostgresEventStoreTests : EventStoreContractTests {
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

  protected override Task<IEventStore> CreateEventStoreAsync() {
    var eventStore = new DapperPostgresEventStore(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IEventStore>(eventStore);
  }

  private class TestFixture : PostgresTestBase { }
}
