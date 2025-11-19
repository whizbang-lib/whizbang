using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperOutbox using SQLite.
/// Inherits all contract tests from OutboxContractTests.
/// </summary>
[InheritsTests]
public class DapperOutboxTests : OutboxContractTests {
  private DapperTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public void Cleanup() {
    _testBase?.Cleanup();
  }

  protected override Task<IOutbox> CreateOutboxAsync() {
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new DapperSqliteOutbox(_testBase.ConnectionFactory, _testBase.Executor, adapter);
    return Task.FromResult<IOutbox>(outbox);
  }

  private class TestFixture : DapperTestBase { }
}
