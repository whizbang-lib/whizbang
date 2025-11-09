using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperInbox using SQLite.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[InheritsTests]
public class DapperInboxTests : InboxContractTests {
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

  protected override Task<IInbox> CreateInboxAsync() {
    var inbox = new DapperSqliteInbox(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IInbox>(inbox);
  }

  private class TestFixture : DapperTestBase { }
}
