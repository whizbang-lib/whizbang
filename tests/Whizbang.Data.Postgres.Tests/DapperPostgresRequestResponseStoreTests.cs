using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresRequestResponseStore using PostgreSQL.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresRequestResponseStoreTests : RequestResponseStoreContractTests {
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

  protected override Task<IRequestResponseStore> CreateStoreAsync() {
    var jsonContext = new WhizbangJsonContext();
    var store = new DapperPostgresRequestResponseStore(_testBase.ConnectionFactory, _testBase.Executor, jsonContext);
    return Task.FromResult<IRequestResponseStore>(store);
  }

  private class TestFixture : PostgresTestBase { }
}
