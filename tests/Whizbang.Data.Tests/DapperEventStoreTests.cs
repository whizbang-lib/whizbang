using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperEventStore using SQLite.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[InheritsTests]
public class DapperEventStoreTests : EventStoreContractTests {
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
    var jsonContext = new WhizbangJsonContext();
    var policyEngine = new PolicyEngine();
    var eventStore = new DapperSqliteEventStore(_testBase.ConnectionFactory, _testBase.Executor, jsonContext, policyEngine);
    return Task.FromResult<IEventStore>(eventStore);
  }

  private class TestFixture : DapperTestBase { }
}
