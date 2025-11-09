using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryEventStore implementation.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[InheritsTests]
public class InMemoryEventStoreTests : EventStoreContractTests {
  protected override Task<IEventStore> CreateEventStoreAsync() {
    return Task.FromResult<IEventStore>(new InMemoryEventStore());
  }
}
