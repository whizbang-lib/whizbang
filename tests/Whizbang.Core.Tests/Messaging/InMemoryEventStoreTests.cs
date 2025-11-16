using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryEventStore implementation.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[InheritsTests]
public class InMemoryEventStoreTests : EventStoreContractTests {
  protected override Task<IEventStore> CreateEventStoreAsync() {
    // Create policy engine with default configuration
    var policyEngine = new PolicyEngine();
    return Task.FromResult<IEventStore>(new InMemoryEventStore(policyEngine));
  }
}
