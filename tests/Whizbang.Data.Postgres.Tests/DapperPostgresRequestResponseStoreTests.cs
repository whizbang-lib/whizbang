using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresRequestResponseStore using PostgreSQL.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// </summary>
[NotInParallel]
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
    var store = new DapperPostgresRequestResponseStore(_testBase.ConnectionFactory, _testBase.Executor);
    return Task.FromResult<IRequestResponseStore>(store);
  }

  private class TestFixture : PostgresTestBase { }
}
