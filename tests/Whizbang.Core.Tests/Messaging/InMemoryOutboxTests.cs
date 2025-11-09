using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryOutbox implementation.
/// Inherits all contract tests from OutboxContractTests.
/// </summary>
[InheritsTests]
public class InMemoryOutboxTests : OutboxContractTests {
  protected override Task<IOutbox> CreateOutboxAsync() {
    return Task.FromResult<IOutbox>(new InMemoryOutbox());
  }
}
