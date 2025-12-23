using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresRequestResponseStore using PostgreSQL.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresRequestResponseStoreTests : RequestResponseStoreContractTests, IDisposable {
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

  protected override Task<IRequestResponseStore> CreateStoreAsync() {
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var store = new DapperPostgresRequestResponseStore(_testBase.ConnectionFactory, _testBase.Executor, jsonOptions);
    return Task.FromResult<IRequestResponseStore>(store);
  }

  private sealed class TestFixture : PostgresTestBase { }
}
