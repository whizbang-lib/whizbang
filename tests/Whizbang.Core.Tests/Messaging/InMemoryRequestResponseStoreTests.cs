using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryRequestResponseStore implementation.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// </summary>
[InheritsTests]
public class InMemoryRequestResponseStoreTests : RequestResponseStoreContractTests {
  protected override Task<IRequestResponseStore> CreateStoreAsync() {
    return Task.FromResult<IRequestResponseStore>(new InMemoryRequestResponseStore());
  }
}
