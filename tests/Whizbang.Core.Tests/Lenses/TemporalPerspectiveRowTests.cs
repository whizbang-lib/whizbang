using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for TemporalPerspectiveRow - row type for temporal (append-only) perspectives.
/// Aligned with SQL Server temporal table patterns and EF Core temporal support.
/// </summary>
[Category("TemporalPerspectives")]
public class TemporalPerspectiveRowTests {
  [Test]
  public async Task TemporalPerspectiveRow_HasRequiredPropertiesAsync() {
    // Arrange
    var id = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var periodStart = DateTime.UtcNow.AddMinutes(-10);
    var periodEnd = DateTime.MaxValue;
    var validTime = DateTimeOffset.UtcNow;

    // Act
    var row = new TemporalPerspectiveRow<ActivityModel> {
      Id = id,
      StreamId = streamId,
      EventId = eventId,
      Data = new ActivityModel { Action = "created", Description = "Test" },
      Metadata = new PerspectiveMetadata { EventType = "TestEvent" },
      Scope = new PerspectiveScope { TenantId = "tenant1" },
      ActionType = TemporalActionType.Insert,
      PeriodStart = periodStart,
      PeriodEnd = periodEnd,
      ValidTime = validTime
    };

    // Assert
    await Assert.That(row.Id).IsEqualTo(id);
    await Assert.That(row.StreamId).IsEqualTo(streamId);
    await Assert.That(row.EventId).IsEqualTo(eventId);
    await Assert.That(row.Data.Action).IsEqualTo("created");
    await Assert.That(row.ActionType).IsEqualTo(TemporalActionType.Insert);
    await Assert.That(row.PeriodStart).IsEqualTo(periodStart);
    await Assert.That(row.PeriodEnd).IsEqualTo(periodEnd);
    await Assert.That(row.ValidTime).IsEqualTo(validTime);
  }

  [Test]
  public async Task TemporalPerspectiveRow_StreamIdTracksAggregateAsync() {
    // Arrange - Each row belongs to a stream (aggregate)
    var orderId = Guid.NewGuid();

    // Act
    var row = _createRow(streamId: orderId);

    // Assert - StreamId identifies the aggregate this entry belongs to
    await Assert.That(row.StreamId).IsEqualTo(orderId);
  }

  [Test]
  public async Task TemporalPerspectiveRow_EventIdTracksSourceEventAsync() {
    // Arrange - Each row is created from a specific event
    var eventId = Guid.NewGuid();

    // Act
    var row = _createRow(eventId: eventId);

    // Assert - EventId tracks which event created this entry
    await Assert.That(row.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task TemporalPerspectiveRow_PeriodStartTracksWhenVersionBecameActiveAsync() {
    // Arrange - SQL Server temporal pattern: SysStartTime
    var periodStart = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    // Act
    var row = _createRow(periodStart: periodStart);

    // Assert - PeriodStart = when this row became the "current" version
    await Assert.That(row.PeriodStart).IsEqualTo(periodStart);
  }

  [Test]
  public async Task TemporalPerspectiveRow_PeriodEndTracksWhenVersionWasSupersededAsync() {
    // Arrange - SQL Server temporal pattern: SysEndTime
    // For current rows, PeriodEnd is DateTime.MaxValue
    var periodEnd = DateTime.MaxValue;

    // Act
    var currentRow = _createRow(periodEnd: periodEnd);

    // Assert - PeriodEnd = DateTime.MaxValue for currently active rows
    await Assert.That(currentRow.PeriodEnd).IsEqualTo(DateTime.MaxValue);
  }

  [Test]
  public async Task TemporalPerspectiveRow_ValidTimeTracksBusinesTimeFromEventAsync() {
    // Arrange - Business time vs system time distinction
    // ValidTime = when the event occurred in business terms
    // PeriodStart = when we recorded it in the database
    var businessTime = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
    var recordedTime = new DateTime(2026, 1, 15, 14, 35, 0, DateTimeKind.Utc); // 5 min later

    // Act
    var row = _createRow(validTime: businessTime, periodStart: recordedTime);

    // Assert - ValidTime from event, PeriodStart from database
    await Assert.That(row.ValidTime).IsEqualTo(businessTime);
    await Assert.That(row.PeriodStart).IsEqualTo(recordedTime);
  }

  [Test]
  public async Task TemporalPerspectiveRow_HasMetadataAndScopeAsync() {
    // Arrange - Same pattern as PerspectiveRow
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreatedEvent",
      EventId = Guid.NewGuid().ToString(),
      CorrelationId = "corr-123"
    };
    var scope = new PerspectiveScope {
      TenantId = "tenant-abc",
      UserId = "user-xyz"
    };

    // Act
    var row = _createRow(metadata: metadata, scope: scope);

    // Assert
    await Assert.That(row.Metadata.EventType).IsEqualTo("OrderCreatedEvent");
    await Assert.That(row.Metadata.CorrelationId).IsEqualTo("corr-123");
    await Assert.That(row.Scope.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(row.Scope.UserId).IsEqualTo("user-xyz");
  }

  private static TemporalPerspectiveRow<ActivityModel> _createRow(
      Guid? id = null,
      Guid? streamId = null,
      Guid? eventId = null,
      TemporalActionType actionType = TemporalActionType.Insert,
      DateTime? periodStart = null,
      DateTime? periodEnd = null,
      DateTimeOffset? validTime = null,
      PerspectiveMetadata? metadata = null,
      PerspectiveScope? scope = null) {
    return new TemporalPerspectiveRow<ActivityModel> {
      Id = id ?? Guid.NewGuid(),
      StreamId = streamId ?? Guid.NewGuid(),
      EventId = eventId ?? Guid.NewGuid(),
      Data = new ActivityModel { Action = "test", Description = "Test entry" },
      Metadata = metadata ?? new PerspectiveMetadata { EventType = "TestEvent" },
      Scope = scope ?? new PerspectiveScope { TenantId = "default" },
      ActionType = actionType,
      PeriodStart = periodStart ?? DateTime.UtcNow,
      PeriodEnd = periodEnd ?? DateTime.MaxValue,
      ValidTime = validTime ?? DateTimeOffset.UtcNow
    };
  }
}

/// <summary>
/// Tests for TemporalActionType enum.
/// </summary>
[Category("TemporalPerspectives")]
public class TemporalActionTypeTests {
  [Test]
  public async Task TemporalActionType_HasInsertValueAsync() {
    // Arrange & Act
    var actionType = TemporalActionType.Insert;

    // Assert - Insert for new entity creation
    await Assert.That((int)actionType).IsEqualTo(0);
    await Assert.That(actionType.ToString()).IsEqualTo("Insert");
  }

  [Test]
  public async Task TemporalActionType_HasUpdateValueAsync() {
    // Arrange & Act
    var actionType = TemporalActionType.Update;

    // Assert - Update for entity modification
    await Assert.That((int)actionType).IsEqualTo(1);
    await Assert.That(actionType.ToString()).IsEqualTo("Update");
  }

  [Test]
  public async Task TemporalActionType_HasDeleteValueAsync() {
    // Arrange & Act
    var actionType = TemporalActionType.Delete;

    // Assert - Delete for soft-delete or removal
    await Assert.That((int)actionType).IsEqualTo(2);
    await Assert.That(actionType.ToString()).IsEqualTo("Delete");
  }

  [Test]
  public async Task TemporalActionType_CanBeUsedInSwitchAsync() {
    // Arrange
    var actionType = TemporalActionType.Update;

    // Act
    var description = actionType switch {
      TemporalActionType.Insert => "Created",
      TemporalActionType.Update => "Modified",
      TemporalActionType.Delete => "Removed",
      _ => "Unknown"
    };

    // Assert
    await Assert.That(description).IsEqualTo("Modified");
  }
}

// Test model for temporal perspective
internal sealed record ActivityModel {
  public required string Action { get; init; }
  public required string Description { get; init; }
}
