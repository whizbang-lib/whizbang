using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresOutbox using PostgreSQL.
/// Inherits all contract tests from OutboxContractTests.
/// </summary>
[NotInParallel]
[InheritsTests]
public class DapperPostgresOutboxTests : OutboxContractTests {
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

  protected override Task<IOutbox> CreateOutboxAsync() {
    var outbox = new DapperPostgresOutbox(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IOutbox>(outbox);
  }

  private class TestFixture : PostgresTestBase { }
}
