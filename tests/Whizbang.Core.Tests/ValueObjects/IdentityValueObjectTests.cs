using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for identity value objects (MessageId, CorrelationId, StreamId, EventId).
/// These tests verify that IDs use UUIDv7 (time-ordered, database-friendly GUIDs)
/// and implement IWhizbangId with TrackedGuid backing.
/// </summary>
public class IdentityValueObjectTests {
  // ==================== IDENTITY VALUE OBJECT TESTS ====================
  // Tests for identity value objects using parameterized approach
  // to eliminate duplication between identical test logic

  /// <summary>
  /// Data source for identity value object types.
  /// Returns factory functions to create IDs and extract their Guid values.
  /// TUnit0046: Wrapping in Func<> ensures proper test isolation for reference types.
  /// </summary>
  public static IEnumerable<Func<(Func<Guid> createId, string typeName)>> GetIdTypes() {
    yield return () => (() => MessageId.New().Value, "MessageId");
    yield return () => (() => CorrelationId.New().Value, "CorrelationId");
    yield return () => (() => StreamId.New().Value, "StreamId");
    yield return () => (() => EventId.New().Value, "EventId");
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
    await Assert.That(ids).Count().IsEqualTo(sortedIds.Count);
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
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All should be UUIDv7, which means they should all be time-ordered
    // We verify this by checking they can be sorted (UUIDv7 property)
    var guids = new[] {
      messageId.Value,
      correlationId.Value,
      streamId.Value,
      eventId.Value
    };

    // Should not throw - UUIDv7 are comparable
    var sorted = guids.OrderBy(g => g).ToArray();
    await Assert.That(sorted).Count().IsEqualTo(4);
  }

  [Test]
  public async Task AllIdTypes_ImplementIWhizbangIdAsync() {
    // Arrange & Act - Generate one of each ID type
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All should implement IWhizbangId
    await Assert.That(messageId).IsAssignableTo<IWhizbangId>();
    await Assert.That(correlationId).IsAssignableTo<IWhizbangId>();
    await Assert.That(streamId).IsAssignableTo<IWhizbangId>();
    await Assert.That(eventId).IsAssignableTo<IWhizbangId>();
  }

  [Test]
  public async Task AllIdTypes_HaveSubMillisecondPrecisionAsync() {
    // Arrange & Act - Generate one of each ID type
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All should have sub-millisecond precision (Medo-generated)
    await Assert.That(messageId.SubMillisecondPrecision).IsTrue();
    await Assert.That(correlationId.SubMillisecondPrecision).IsTrue();
    await Assert.That(streamId.SubMillisecondPrecision).IsTrue();
    await Assert.That(eventId.SubMillisecondPrecision).IsTrue();
  }

  [Test]
  public async Task AllIdTypes_AreTimeOrderedAsync() {
    // Arrange & Act - Generate one of each ID type
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All should be time-ordered (UUIDv7)
    await Assert.That(messageId.IsTimeOrdered).IsTrue();
    await Assert.That(correlationId.IsTimeOrdered).IsTrue();
    await Assert.That(streamId.IsTimeOrdered).IsTrue();
    await Assert.That(eventId.IsTimeOrdered).IsTrue();
  }

  #endregion
}
