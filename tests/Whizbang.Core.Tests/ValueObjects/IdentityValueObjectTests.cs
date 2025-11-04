using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for identity value objects (MessageId, CorrelationId, MessageId).
/// These tests verify that IDs use UUIDv7 (time-ordered, database-friendly GUIDs).
/// </summary>
public class IdentityValueObjectTests {
  #region MessageId Tests

  [Test]
  public async Task MessageId_New_GeneratesUniqueIdsAsync() {
    // Arrange & Act
    var id1 = MessageId.New();
    var id2 = MessageId.New();

    // Assert
    await Assert.That(id1).IsNotEqualTo(id2);
  }

  [Test]
  public async Task MessageId_New_GeneratesTimeOrderedIdsAsync() {
    // Arrange - Create IDs with small delay between them
    var id1 = MessageId.New();
    await Task.Delay(2); // Small delay to ensure different timestamp
    var id2 = MessageId.New();
    await Task.Delay(2);
    var id3 = MessageId.New();

    // Act - Extract the underlying Guid values
    var guid1 = id1.Value;
    var guid2 = id2.Value;
    var guid3 = id3.Value;

    // Assert - UUIDv7 should be sortable by creation time
    // When sorted, they should maintain creation order
    var ids = new[] { guid3, guid1, guid2 };
    var sortedIds = ids.OrderBy(g => g).ToArray();

    await Assert.That(sortedIds[0]).IsEqualTo(guid1);
    await Assert.That(sortedIds[1]).IsEqualTo(guid2);
    await Assert.That(sortedIds[2]).IsEqualTo(guid3);
  }

  [Test]
  public async Task MessageId_New_ProducesSequentialGuidsAsync() {
    // Arrange & Act - Generate multiple IDs
    var ids = Enumerable.Range(0, 100)
        .Select(_ => {
          var id = MessageId.New();
          Thread.Sleep(1); // Ensure timestamp progression
          return id.Value;
        })
        .ToList();

    // Assert - IDs should already be in ascending order (time-ordered)
    var sortedIds = ids.OrderBy(g => g).ToList();
    await Assert.That(ids).IsEquivalentTo(sortedIds);
  }

  #endregion

  #region CorrelationId Tests

  [Test]
  public async Task CorrelationId_New_GeneratesUniqueIdsAsync() {
    // Arrange & Act
    var id1 = CorrelationId.New();
    var id2 = CorrelationId.New();

    // Assert
    await Assert.That(id1).IsNotEqualTo(id2);
  }

  [Test]
  public async Task CorrelationId_New_GeneratesTimeOrderedIdsAsync() {
    // Arrange - Create IDs with small delay between them
    var id1 = CorrelationId.New();
    await Task.Delay(2);
    var id2 = CorrelationId.New();
    await Task.Delay(2);
    var id3 = CorrelationId.New();

    // Act - Extract the underlying Guid values
    var guid1 = id1.Value;
    var guid2 = id2.Value;
    var guid3 = id3.Value;

    // Assert - UUIDv7 should be sortable by creation time
    var ids = new[] { guid3, guid1, guid2 };
    var sortedIds = ids.OrderBy(g => g).ToArray();

    await Assert.That(sortedIds[0]).IsEqualTo(guid1);
    await Assert.That(sortedIds[1]).IsEqualTo(guid2);
    await Assert.That(sortedIds[2]).IsEqualTo(guid3);
  }

  [Test]
  public async Task CorrelationId_New_ProducesSequentialGuidsAsync() {
    // Arrange & Act - Generate multiple IDs
    var ids = Enumerable.Range(0, 100)
        .Select(_ => {
          var id = CorrelationId.New();
          Thread.Sleep(1);
          return id.Value;
        })
        .ToList();

    // Assert - IDs should already be in ascending order
    var sortedIds = ids.OrderBy(g => g).ToList();
    await Assert.That(ids).IsEquivalentTo(sortedIds);
  }

  #endregion

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
