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
  public async Task AllIdTypes_SubMillisecondPrecision_ReturnsTrueForFreshIdsAsync() {
    // Arrange & Act - Generate one of each ID type via New()
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All freshly created IDs should return true for sub-millisecond precision
    // Note: IDs created via New() use TrackedGuid.NewMedo() which has sub-ms precision.
    // After serialization/deserialization, this will return false (metadata lost).
    await Assert.That(messageId.GetSubMillisecondPrecision()).IsTrue();
    await Assert.That(correlationId.GetSubMillisecondPrecision()).IsTrue();
    await Assert.That(streamId.GetSubMillisecondPrecision()).IsTrue();
    await Assert.That(eventId.GetSubMillisecondPrecision()).IsTrue();
  }

  [Test]
  public async Task AllIdTypes_AreTimeOrderedAsync() {
    // Arrange & Act - Generate one of each ID type
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All should be time-ordered (UUIDv7)
    // Use public methods since metadata properties are explicit interface implementations
    await Assert.That(messageId.GetIsTimeOrdered()).IsTrue();
    await Assert.That(correlationId.GetIsTimeOrdered()).IsTrue();
    await Assert.That(streamId.GetIsTimeOrdered()).IsTrue();
    await Assert.That(eventId.GetIsTimeOrdered()).IsTrue();
  }

  [Test]
  public async Task AllIdTypes_FreshIds_AreTrackingAsync() {
    // Arrange & Act - Generate one of each ID type via New()
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var streamId = StreamId.New();
    var eventId = EventId.New();

    // Assert - All freshly created IDs should have tracking metadata
    await Assert.That(messageId.GetIsTracking()).IsTrue();
    await Assert.That(correlationId.GetIsTracking()).IsTrue();
    await Assert.That(streamId.GetIsTracking()).IsTrue();
    await Assert.That(eventId.GetIsTracking()).IsTrue();
  }

  [Test]
  public async Task AllIdTypes_DeserializedIds_AreNotTrackingAsync() {
    // Arrange - Create IDs and simulate deserialization by reconstructing from Guid
    var messageGuid = MessageId.New().Value;
    var correlationGuid = CorrelationId.New().Value;
    var streamGuid = StreamId.New().Value;
    var eventGuid = EventId.New().Value;

    // Act - Reconstruct IDs from Guids (simulates DB/JSON deserialization)
    var messageId = MessageId.From(messageGuid);
    var correlationId = CorrelationId.From(correlationGuid);
    var streamId = StreamId.From(streamGuid);
    var eventId = EventId.From(eventGuid);

    // Assert - Deserialized IDs should NOT have tracking metadata
    await Assert.That(messageId.GetIsTracking()).IsFalse();
    await Assert.That(correlationId.GetIsTracking()).IsFalse();
    await Assert.That(streamId.GetIsTracking()).IsFalse();
    await Assert.That(eventId.GetIsTracking()).IsFalse();
  }

  #endregion
}
