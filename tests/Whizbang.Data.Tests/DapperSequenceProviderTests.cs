using Whizbang.Core.Sequencing;
using Whizbang.Data.Dapper.Sqlite;
using Whizbang.Sequencing.Tests;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperSequenceProvider using SQLite.
/// Inherits all contract tests from SequenceProviderContractTests.
/// </summary>
[InheritsTests]
public class DapperSequenceProviderTests : SequenceProviderContractTests, IDisposable {
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

  protected override ISequenceProvider CreateProvider() {
    return new DapperSqliteSequenceProvider(_testBase.ConnectionFactory, _testBase.Executor);
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  private sealed class TestFixture : DapperTestBase { }
}
