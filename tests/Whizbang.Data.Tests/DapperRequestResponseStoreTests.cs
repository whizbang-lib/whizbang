using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperRequestResponseStore using SQLite.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// </summary>
[InheritsTests]
public class DapperRequestResponseStoreTests : RequestResponseStoreContractTests, IDisposable {
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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var store = new DapperSqliteRequestResponseStore(_testBase.ConnectionFactory, _testBase.Executor, jsonOptions);
    return Task.FromResult<IRequestResponseStore>(store);
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  private sealed class TestFixture : DapperTestBase { }
}
