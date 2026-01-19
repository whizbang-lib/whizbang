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

  // Skip tests that require 3-column JSONB storage model (SQLite uses single envelope column)
  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task GetEventsBetweenAsync_ShouldReturnEventsInRangeAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task GetEventsBetweenAsync_EmptyStream_ShouldReturnEmptyListAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task GetEventsBetweenPolymorphicAsync_ShouldReturnIEventEnvelopesAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task GetEventsBetweenPolymorphicAsync_EmptyStream_ShouldReturnEmptyListAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task GetEventsBetweenPolymorphicAsync_WithNullEventTypes_ShouldThrowAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task ReadPolymorphicAsync_ShouldReturnIEventEnvelopesAsync() => Task.CompletedTask;

  [Test]
  [Skip("SQLite uses single envelope column, not 3-column JSONB model")]
  public override Task ReadPolymorphicAsync_EmptyStream_ShouldReturnEmptyAsync() => Task.CompletedTask;

  private sealed class TestFixture : DapperTestBase { }
}
