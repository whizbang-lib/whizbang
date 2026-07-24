using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for IPerspectiveStore default interface method implementations.
/// Verifies that the default implementations delegate correctly.
/// </summary>
[Category("Perspectives")]
public class IPerspectiveStoreTests {
  [Test]
  public async Task UpsertAsync_WithScope_DefaultImplementation_DelegatesToUpsertWithoutScopeAsync() {
    // Arrange - Tests the default interface method on line 52 of IPerspectiveStore.cs
    var store = new TestPerspectiveStore();
    var streamId = Guid.NewGuid();
    var model = new TestModel { Name = "test" };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    // Act - Cast to interface to call the default interface method
    IPerspectiveStore<TestModel> iface = store;
    await iface.UpsertAsync(streamId, model, scope);

    // Assert - The base UpsertAsync was called (scope ignored by default)
    await Assert.That(store.UpsertCallCount).IsEqualTo(1);
    await Assert.That(store.LastStreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WithScope_DefaultImplementation_DelegatesToWithoutScopeAsync() {
    // Arrange - Tests the default interface method on line 108 of IPerspectiveStore.cs
    var store = new TestPerspectiveStore();
    const string partitionKey = "partition-key-1";
    var model = new TestModel { Name = "test" };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    // Act - Cast to interface to call the default interface method
    IPerspectiveStore<TestModel> iface = store;
    await iface.UpsertByPartitionKeyAsync(partitionKey, model, scope);

    // Assert - The base UpsertByPartitionKeyAsync was called (scope ignored by default)
    await Assert.That(store.UpsertByPartitionKeyCallCount).IsEqualTo(1);
    await Assert.That(store.LastPartitionKey).IsEqualTo("partition-key-1");
  }

  #region Test Types

  private sealed class TestModel {
    public required string Name { get; init; }
  }

  /// <summary>
  /// Test double implementing IPerspectiveStore but NOT overriding the default
  /// interface methods for UpsertAsync(scope) and UpsertByPartitionKeyAsync(scope),
  /// so the default implementations are exercised.
  /// </summary>
  private sealed class TestPerspectiveStore : IPerspectiveStore<TestModel> {
    public int UpsertCallCount { get; private set; }
    public int UpsertByPartitionKeyCallCount { get; private set; }
    public Guid LastStreamId { get; private set; }
    public string? LastPartitionKey { get; private set; }

    public Task<TestModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult<TestModel?>(null);

    public Task UpsertAsync(Guid streamId, TestModel model, CancellationToken cancellationToken = default) {
      UpsertCallCount++;
      LastStreamId = streamId;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TestModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null,
      CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<TestModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
      Task.FromResult<TestModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TestModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull {
      UpsertByPartitionKeyCallCount++;
      LastPartitionKey = partitionKey?.ToString();
      return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }

  #endregion
}
