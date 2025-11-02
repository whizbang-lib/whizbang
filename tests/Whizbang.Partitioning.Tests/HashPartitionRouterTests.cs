using TUnit.Core;
using Whizbang.Core.Partitioning;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// Tests for HashPartitionRouter implementation.
/// Inherits contract tests to ensure compliance with IPartitionRouter requirements.
/// </summary>
[Category("Partitioning")]
[InheritsTests]
public class HashPartitionRouterTests : PartitionRouterContractTests {
  /// <summary>
  /// Creates a HashPartitionRouter for testing.
  /// </summary>
  protected override IPartitionRouter CreateRouter() {
    return new HashPartitionRouter();
  }
}
