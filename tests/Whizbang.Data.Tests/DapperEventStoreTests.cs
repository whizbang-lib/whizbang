using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperEventStore using SQLite.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[InheritsTests]
public class DapperEventStoreTests : EventStoreContractTests, IDisposable {
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

  protected override Task<IEventStore> CreateEventStoreAsync() {
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var policyEngine = new PolicyEngine();
    var eventStore = new DapperSqliteEventStore(_testBase.ConnectionFactory, _testBase.Executor, jsonOptions, policyEngine);
    return Task.FromResult<IEventStore>(eventStore);
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  private sealed class TestFixture : DapperTestBase { }
}
