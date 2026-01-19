using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for PerspectiveCheckpointRecord class.
/// Verifies checkpoint tracking behavior for perspective processing.
/// </summary>
[Category("Messaging")]
[Category("Perspectives")]
public class PerspectiveCheckpointRecordTests {

  [Test]
  public async Task PerspectiveCheckpointRecord_WithAllProperties_CreatesInstanceAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var perspectiveName = "OrderSummaryPerspective";
    var lastEventId = Guid.NewGuid();
    var status = PerspectiveProcessingStatus.Completed;
    var processedAt = DateTime.UtcNow;

    // Act
    var checkpoint = new PerspectiveCheckpointRecord {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = lastEventId,
      Status = status,
      ProcessedAt = processedAt
    };

    // Assert
    await Assert.That(checkpoint.StreamId).IsEqualTo(streamId);
    await Assert.That(checkpoint.PerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(checkpoint.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(checkpoint.Status).IsEqualTo(status);
    await Assert.That(checkpoint.ProcessedAt).IsEqualTo(processedAt);
  }

  [Test]
  public async Task PerspectiveCheckpointRecord_WithError_StoresErrorMessageAsync() {
    // Arrange & Act
    var checkpoint = new PerspectiveCheckpointRecord {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "OrderSummaryPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      ProcessedAt = DateTime.UtcNow,
      Error = "Database connection failed"
    };

    // Assert
    await Assert.That(checkpoint.Status).IsEqualTo(PerspectiveProcessingStatus.Failed);
    await Assert.That(checkpoint.Error).IsEqualTo("Database connection failed");
  }

  [Test]
  public async Task PerspectiveCheckpointRecord_UpdatesProcessedAt_WhenCheckpointAdvancesAsync() {
    // Arrange
    var checkpoint = new PerspectiveCheckpointRecord {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "OrderSummaryPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
      ProcessedAt = DateTime.UtcNow.AddMinutes(-5)
    };

    var originalProcessedAt = checkpoint.ProcessedAt;
    var newEventId = Guid.NewGuid();

    // Act
    checkpoint.LastEventId = newEventId;
    checkpoint.ProcessedAt = DateTime.UtcNow;

    // Assert
    await Assert.That(checkpoint.LastEventId).IsEqualTo(newEventId);
    await Assert.That(checkpoint.ProcessedAt).IsGreaterThan(originalProcessedAt);
  }

  [Test]
  public async Task PerspectiveCheckpointRecord_Serialization_RoundTripsCorrectlyAsync() {
    // Arrange
    var checkpoint = new PerspectiveCheckpointRecord {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "OrderSummaryPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
      ProcessedAt = DateTime.UtcNow,
      Error = null
    };

    // Act
    var json = JsonSerializer.Serialize(checkpoint);
    var deserialized = JsonSerializer.Deserialize<PerspectiveCheckpointRecord>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.StreamId).IsEqualTo(checkpoint.StreamId);
    await Assert.That(deserialized.PerspectiveName).IsEqualTo(checkpoint.PerspectiveName);
    await Assert.That(deserialized.LastEventId).IsEqualTo(checkpoint.LastEventId);
    await Assert.That(deserialized.Status).IsEqualTo(checkpoint.Status);
    await Assert.That(deserialized.ProcessedAt).IsEqualTo(checkpoint.ProcessedAt);
    await Assert.That(deserialized.Error).IsEqualTo(checkpoint.Error);
  }

  [Test]
  public async Task PerspectiveCheckpointRecord_WithUUIDv7EventId_MaintainsTemporalOrderAsync() {
    // Arrange
    // UUIDv7 embeds timestamp - later events have larger IDs
    var earlierEventId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-10));
    var laterEventId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

    var checkpoint1 = new PerspectiveCheckpointRecord {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "OrderSummaryPerspective",
      LastEventId = earlierEventId,
      Status = PerspectiveProcessingStatus.Completed,
      ProcessedAt = DateTime.UtcNow.AddMinutes(-10)
    };

    var checkpoint2 = new PerspectiveCheckpointRecord {
      StreamId = checkpoint1.StreamId,
      PerspectiveName = "OrderSummaryPerspective",
      LastEventId = laterEventId,
      Status = PerspectiveProcessingStatus.Completed,
      ProcessedAt = DateTime.UtcNow
    };

    // Act & Assert
    await Assert.That(laterEventId).IsGreaterThan(earlierEventId);
    await Assert.That(checkpoint2.LastEventId).IsGreaterThan(checkpoint1.LastEventId);
  }
}
