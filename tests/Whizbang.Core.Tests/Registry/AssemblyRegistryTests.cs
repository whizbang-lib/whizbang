using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Registry;

/// <summary>
/// Tests for AssemblyRegistry - thread-safe registry for multi-assembly contributions.
/// Tests verify registration, ordering, caching, thread safety, and error handling.
/// </summary>
/// <remarks>
/// These tests must run sequentially (NotInParallel) because they test global static state.
/// Each test clears and modifies the shared AssemblyRegistry.
/// </remarks>
[Category("Core")]
[Category("Registry")]
[NotInParallel("AssemblyRegistry")]
public class AssemblyRegistryTests {
  /// <summary>
  /// Test contribution type for registry testing.
  /// </summary>
  private interface ITestContribution { }

  /// <summary>
  /// Simple test contribution implementation.
  /// </summary>
  private sealed class TestContribution(string name) : ITestContribution {
    public string Name { get; } = name;
  }

  [Test]
  public async Task Register_WithValidContribution_ShouldIncreaseCountAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var contribution = new TestContribution("test1");

    // Act
    AssemblyRegistry<ITestContribution>.Register(contribution);

    // Assert
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Register_WithNullContribution_ShouldThrowArgumentNullExceptionAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();

    // Act & Assert
    ArgumentNullException? caughtException = null;
    try {
      AssemblyRegistry<ITestContribution>.Register(null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task Register_WithPriority_ShouldStoreContributionAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var contribution = new TestContribution("priorityTest");

    // Act
    AssemblyRegistry<ITestContribution>.Register(contribution, priority: 500);

    // Assert
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(1);
    var contributions = AssemblyRegistry<ITestContribution>.GetOrderedContributions();
    await Assert.That(contributions).Count().IsEqualTo(1);
  }

  [Test]
  public async Task GetOrderedContributions_WithMultipleContributions_ShouldReturnOrderedByPriorityAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var lowPriority = new TestContribution("low");
    var highPriority = new TestContribution("high");
    var mediumPriority = new TestContribution("medium");

    // Register out of order
    AssemblyRegistry<ITestContribution>.Register(mediumPriority, priority: 500);
    AssemblyRegistry<ITestContribution>.Register(highPriority, priority: 100);
    AssemblyRegistry<ITestContribution>.Register(lowPriority, priority: 1000);

    // Act
    var contributions = AssemblyRegistry<ITestContribution>.GetOrderedContributions();

    // Assert
    await Assert.That(contributions).Count().IsEqualTo(3);
    // Lower priority comes first
    await Assert.That(((TestContribution)contributions[0]).Name).IsEqualTo("high");
    await Assert.That(((TestContribution)contributions[1]).Name).IsEqualTo("medium");
    await Assert.That(((TestContribution)contributions[2]).Name).IsEqualTo("low");
  }

  [Test]
  public async Task GetOrderedContributions_CalledMultipleTimes_ShouldReturnCachedResultAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var contribution = new TestContribution("cached");
    AssemblyRegistry<ITestContribution>.Register(contribution);

    // Act - Call multiple times
    var first = AssemblyRegistry<ITestContribution>.GetOrderedContributions();
    var second = AssemblyRegistry<ITestContribution>.GetOrderedContributions();

    // Assert - Should return the same cached list
    await Assert.That(ReferenceEquals(first, second)).IsTrue();
  }

  [Test]
  public async Task Register_AfterGetOrderedContributions_ShouldInvalidateCacheAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var contribution1 = new TestContribution("first");
    AssemblyRegistry<ITestContribution>.Register(contribution1);
    var firstResult = AssemblyRegistry<ITestContribution>.GetOrderedContributions();

    // Act - Register new contribution after getting ordered list
    var contribution2 = new TestContribution("second");
    AssemblyRegistry<ITestContribution>.Register(contribution2);
    var secondResult = AssemblyRegistry<ITestContribution>.GetOrderedContributions();

    // Assert - Should be different lists (cache invalidated)
    await Assert.That(ReferenceEquals(firstResult, secondResult)).IsFalse();
    await Assert.That(secondResult).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ClearForTesting_ShouldRemoveAllContributionsAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    AssemblyRegistry<ITestContribution>.Register(new TestContribution("test1"));
    AssemblyRegistry<ITestContribution>.Register(new TestContribution("test2"));
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(2);

    // Act
    AssemblyRegistry<ITestContribution>.ClearForTesting();

    // Assert
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetOrderedContributions_WithEmptyRegistry_ShouldReturnEmptyListAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();

    // Act
    var contributions = AssemblyRegistry<ITestContribution>.GetOrderedContributions();

    // Assert
    await Assert.That(contributions).IsNotNull();
    await Assert.That(contributions).Count().IsEqualTo(0);
  }

  [Test]
  public async Task Register_WithDefaultPriority_ShouldUse1000Async() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var contractsLevel = new TestContribution("contracts");
    var defaultLevel = new TestContribution("default");

    // Act - Register with explicit 100 priority (contracts) and default (1000)
    AssemblyRegistry<ITestContribution>.Register(contractsLevel, priority: 100);
    AssemblyRegistry<ITestContribution>.Register(defaultLevel); // Default is 1000

    // Assert - Contracts (100) should come before default (1000)
    var contributions = AssemblyRegistry<ITestContribution>.GetOrderedContributions();
    await Assert.That(((TestContribution)contributions[0]).Name).IsEqualTo("contracts");
    await Assert.That(((TestContribution)contributions[1]).Name).IsEqualTo("default");
  }

  [Test]
  public async Task Count_ShouldReflectNumberOfRegistrationsAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();

    // Act & Assert
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(0);

    AssemblyRegistry<ITestContribution>.Register(new TestContribution("1"));
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(1);

    AssemblyRegistry<ITestContribution>.Register(new TestContribution("2"));
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(2);

    AssemblyRegistry<ITestContribution>.Register(new TestContribution("3"));
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(3);
  }

  [Test]
  public async Task ConcurrentRegistrations_ShouldBeThreadSafeAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    var tasks = new List<Task>();
    var contributionCount = 100;

    // Act - Register contributions from multiple threads
    for (int i = 0; i < contributionCount; i++) {
      var index = i;
      tasks.Add(Task.Run(() =>
          AssemblyRegistry<ITestContribution>.Register(new TestContribution($"concurrent-{index}"))));
    }

    await Task.WhenAll(tasks);

    // Assert
    await Assert.That(AssemblyRegistry<ITestContribution>.Count).IsEqualTo(contributionCount);
    var contributions = AssemblyRegistry<ITestContribution>.GetOrderedContributions();
    await Assert.That(contributions).Count().IsEqualTo(contributionCount);
  }

  [Test]
  public async Task ConcurrentGetOrderedContributions_ShouldBeThreadSafeAsync() {
    // Arrange
    AssemblyRegistry<ITestContribution>.ClearForTesting();
    for (int i = 0; i < 10; i++) {
      AssemblyRegistry<ITestContribution>.Register(new TestContribution($"item-{i}"), priority: i * 100);
    }

    // Act - Get ordered contributions from multiple threads
    var tasks = new List<Task<IReadOnlyList<ITestContribution>>>();
    for (int i = 0; i < 50; i++) {
      tasks.Add(Task.Run(() => AssemblyRegistry<ITestContribution>.GetOrderedContributions()));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All results should have the same items
    foreach (var result in results) {
      await Assert.That(result).Count().IsEqualTo(10);
    }
  }
}
