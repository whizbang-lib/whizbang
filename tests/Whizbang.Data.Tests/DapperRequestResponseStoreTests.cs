using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperRequestResponseStore using SQLite.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// </summary>
[InheritsTests]
public class DapperRequestResponseStoreTests : RequestResponseStoreContractTests {
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

  protected override Task<IRequestResponseStore> CreateStoreAsync() {
    var store = new DapperSqliteRequestResponseStore(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IRequestResponseStore>(store);
  }

  private class TestFixture : DapperTestBase { }
}
