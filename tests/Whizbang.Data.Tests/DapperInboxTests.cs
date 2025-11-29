using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperInbox using SQLite.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[InheritsTests]
public class DapperInboxTests : InboxContractTests {
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

  protected override Task<IInbox> CreateInboxAsync() {
    // Use Core.Tests JSON context which includes TestMessage
    var jsonOptions = global::Whizbang.Core.Tests.Generated.WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var inbox = new DapperSqliteInbox(_testBase.ConnectionFactory, _testBase.Executor, adapter);
    return Task.FromResult<IInbox>(inbox);
  }

  private class TestFixture : DapperTestBase { }
}
