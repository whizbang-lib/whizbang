using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresInbox using PostgreSQL.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[NotInParallel]
[InheritsTests]
public class DapperPostgresInboxTests : InboxContractTests {
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

  protected override Task<IInbox> CreateInboxAsync() {
    var inbox = new DapperPostgresInbox(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IInbox>(inbox);
  }

  private class TestFixture : PostgresTestBase { }
}
