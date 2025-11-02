using TUnit.Core;
using Whizbang.Core.Sequencing;

namespace Whizbang.Sequencing.Tests;

/// <summary>
/// Tests for InMemorySequenceProvider implementation.
/// Inherits contract tests to ensure compliance with ISequenceProvider requirements.
/// </summary>
[Category("Sequencing")]
[InheritsTests]
public class InMemorySequenceProviderTests : SequenceProviderContractTests {
  /// <summary>
  /// Creates an InMemorySequenceProvider for testing.
  /// </summary>
  protected override ISequenceProvider CreateProvider() {
    return new InMemorySequenceProvider();
  }
}
