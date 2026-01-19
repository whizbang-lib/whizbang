using Whizbang.Core.Sequencing;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Sequencing.Tests;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresSequenceProvider using PostgreSQL.
/// Inherits all contract tests from SequenceProviderContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresSequenceProviderTests : SequenceProviderContractTests, IDisposable {
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

  protected override ISequenceProvider CreateProvider() {
    return new DapperPostgresSequenceProvider(_testBase.ConnectionFactory, _testBase.Executor);
  }

  private sealed class TestFixture : PostgresTestBase { }
}
