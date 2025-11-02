using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Observability.Tests.TraceStore;

/// <summary>
/// Tests for InMemoryTraceStore implementation.
/// Inherits all contract tests from TraceStoreContractTests.
/// </summary>
[Category("Observability")]
[InheritsTests]
public class InMemoryTraceStoreTests : TraceStoreContractTests {
  protected override ITraceStore CreateTraceStore() {
    return new InMemoryTraceStore();
  }
}
