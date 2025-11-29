using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresInbox using PostgreSQL.
/// Inherits all contract tests from InboxContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresInboxTests : InboxContractTests {
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

  protected override Task<IInbox> CreateInboxAsync() {
    // Use Core.Tests JSON context which includes TestMessage
    var jsonOptions = global::Whizbang.Core.Tests.Generated.WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var inbox = new DapperPostgresInbox(_testBase.ConnectionFactory, _testBase.Executor, adapter);
    return Task.FromResult<IInbox>(inbox);
  }

  private class TestFixture : PostgresTestBase { }
}
