using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryInbox implementation.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[InheritsTests]
public class InMemoryInboxTests : InboxContractTests {
  protected override Task<IInbox> CreateInboxAsync() {
    return Task.FromResult<IInbox>(new InMemoryInbox());
  }
}
