using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryOutbox implementation.
/// Inherits all contract tests from OutboxContractTests.
/// </summary>
[InheritsTests]
public class InMemoryOutboxTests : OutboxContractTests {
  protected override Task<IOutbox> CreateOutboxAsync() {
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    return Task.FromResult<IOutbox>(new InMemoryOutbox(adapter));
  }
}
