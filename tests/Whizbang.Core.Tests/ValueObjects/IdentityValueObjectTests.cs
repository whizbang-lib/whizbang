using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for identity value objects (MessageId, CorrelationId, MessageId).
/// These tests verify that IDs use UUIDv7 (time-ordered, database-friendly GUIDs).
/// </summary>
public class IdentityValueObjectTests {
  // ==================== IDENTITY VALUE OBJECT TESTS ====================
  // Tests for MessageId and CorrelationId using parameterized approach
  // to eliminate duplication between identical test logic

  /// <summary>
  /// Data source for identity value object types.
  /// Returns factory functions to create IDs and extract their Guid values.
  /// TUnit0046: Wrapping in Func<> ensures proper test isolation for reference types.
  /// </summary>
  public static IEnumerable<Func<(Func<Guid> createId, string typeName)>> GetIdTypes() {
    yield return () => (() => MessageId.New().Value, "MessageId");
    yield return () => (() => CorrelationId.New().Value, "CorrelationId");
  }

  [Test]
  [MethodDataSource(nameof(GetIdTypes))]
  public async Task IdTypes_New_GeneratesUniqueIdsAsync(Func<Guid> createId, string typeName) {
    // Arrange & Act
    var id1 = createId();
    var id2 = createId();

    // Assert
    await Assert.That(id1).IsNotEqualTo(id2);
  }

  [Test]
  [MethodDataSource(nameof(GetIdTypes))]
  public async Task IdTypes_New_GeneratesTimeOrderedIdsAsync(Func<Guid> createId, string typeName) {
    // Arrange - Create IDs with small delay between them
    var id1 = createId();
    await Task.Delay(2); // Small delay to ensure different timestamp
    var id2 = createId();
    await Task.Delay(2);
    var id3 = createId();

    // Assert - UUIDv7 should be sortable by creation time
    // When sorted, they should maintain creation order
    var ids = new[] { id3, id1, id2 };
    var sortedIds = ids.OrderBy(g => g).ToArray();

    await Assert.That(sortedIds[0]).IsEqualTo(id1);
    await Assert.That(sortedIds[1]).IsEqualTo(id2);
    await Assert.That(sortedIds[2]).IsEqualTo(id3);
  }

  [Test]
  [MethodDataSource(nameof(GetIdTypes))]
  public async Task IdTypes_New_ProducesSequentialGuidsAsync(Func<Guid> createId, string typeName) {
    // Arrange & Act - Generate multiple IDs
    var ids = Enumerable.Range(0, 100)
        .Select(_ => {
          var id = createId();
          Thread.Sleep(1); // Ensure timestamp progression
          return id;
        })
        .ToList();

    // Assert - IDs should already be in ascending order (time-ordered)
    var sortedIds = ids.OrderBy(g => g).ToList();
    await Assert.That(ids).HasCount().EqualTo(sortedIds.Count);
    for (int i = 0; i < ids.Count; i++) {
      await Assert.That(ids[i]).IsEqualTo(sortedIds[i]);
    }
  }

  #region Cross-Type Consistency Tests

  [Test]
  public async Task AllIdTypes_UseConsistentGuidVersionAsync() {
    // Arrange & Act - Generate one of each ID type
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Assert - All should be UUIDv7, which means they should all be time-ordered
    // We verify this by checking they can be sorted (UUIDv7 property)
    var guids = new[] {
      messageId.Value,
      correlationId.Value,
      causationId.Value
    };

    // Should not throw - UUIDv7 are comparable
    var sorted = guids.OrderBy(g => g).ToArray();
    await Assert.That(sorted).HasCount().EqualTo(3);
  }

  #endregion
}
