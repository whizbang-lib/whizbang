using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for WhizbangIdProvider - global ID generation configuration.
/// Validates static provider management and custom provider support.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("IdGeneration")]
public class WhizbangIdProviderTests {

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task SetProvider_WithValidProvider_ShouldUseCustomProviderAsync() {
    // Arrange
    var expectedGuid = Guid.Parse("12345678-1234-5678-1234-567812345678");
    var testProvider = new TestIdProvider(expectedGuid);

    try {
      // Act
      WhizbangIdProvider.SetProvider(testProvider);
      var result = WhizbangIdProvider.NewGuid();

      // Assert
      await Assert.That(result).IsEqualTo(expectedGuid);
    } finally {
      // Restore default provider to avoid test pollution
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task SetProvider_WithNullProvider_ShouldThrowArgumentNullExceptionAsync() {
    // Arrange & Act & Assert
    await Assert.That(() => WhizbangIdProvider.SetProvider(null!))
      .ThrowsExactly<ArgumentNullException>()
      .WithParameterName("provider");
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task NewGuid_WithDefaultProvider_ShouldReturnUuidV7Async() {
    // Arrange
    // Ensure default provider is in place
    WhizbangIdProvider.SetProvider(new Uuid7IdProvider());

    // Act
    var result = WhizbangIdProvider.NewGuid();

    // Assert - UUIDv7 has version bits set to 0x7 in the most significant 4 bits of byte 7
    var bytes = result.ToByteArray();
    var version = (bytes[7] & 0xF0) >> 4;

    await Assert.That(version).IsEqualTo(0x7);
    await Assert.That(result).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task NewGuid_WithCustomProvider_ShouldReturnCustomGuidAsync() {
    // Arrange
    var expectedGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    var customProvider = new TestIdProvider(expectedGuid);

    try {
      // Act
      WhizbangIdProvider.SetProvider(customProvider);
      var result = WhizbangIdProvider.NewGuid();

      // Assert
      await Assert.That(result).IsEqualTo(expectedGuid);
    } finally {
      // Restore default provider
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task SetProvider_CalledMultipleTimes_ShouldUseLatestProviderAsync() {
    // Arrange
    var firstGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var secondGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    var providerA = new TestIdProvider(firstGuid);
    var providerB = new TestIdProvider(secondGuid);

    try {
      // Act
      WhizbangIdProvider.SetProvider(providerA);
      var firstResult = WhizbangIdProvider.NewGuid();

      WhizbangIdProvider.SetProvider(providerB);
      var secondResult = WhizbangIdProvider.NewGuid();

      // Assert
      await Assert.That(firstResult).IsEqualTo(firstGuid);
      await Assert.That(secondResult).IsEqualTo(secondGuid);
    } finally {
      // Restore default provider
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  public async Task NewGuid_ThreadSafety_ShouldHandleConcurrentCallsAsync() {
    // Arrange
    WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    const int taskCount = 10;
    const int iterationsPerTask = 100;

    // Act - Call NewGuid from multiple parallel tasks
    var tasks = Enumerable.Range(0, taskCount).Select(async _ => {
      var guids = new List<Guid>();
      for (int i = 0; i < iterationsPerTask; i++) {
        guids.Add(WhizbangIdProvider.NewGuid());
      }
      return guids;
    });

    var results = await Task.WhenAll(tasks);

    // Assert
    // All tasks completed without exception
    await Assert.That(results).HasCount().EqualTo(taskCount);

    // All GUIDs are unique
    var allGuids = results.SelectMany(g => g).ToList();
    await Assert.That(allGuids).HasCount().EqualTo(taskCount * iterationsPerTask);
    await Assert.That(allGuids.Distinct()).HasCount().EqualTo(taskCount * iterationsPerTask);

    // All GUIDs are non-empty
    await Assert.That(allGuids).DoesNotContain(Guid.Empty);
  }

  // Custom test provider for testing
  private class TestIdProvider : IWhizbangIdProvider {
    private readonly Guid _fixedGuid;

    public TestIdProvider(Guid fixedGuid) {
      _fixedGuid = fixedGuid;
    }

    public Guid NewGuid() => _fixedGuid;
  }
}
