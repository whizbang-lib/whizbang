using System.Threading.Channels;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IPerspectiveChannelWriter and PerspectiveChannelWriter.
/// Validates channel-based perspective work distribution pattern.
/// </summary>
public class PerspectiveChannelWriterTests {

  [Test]
  public async Task Constructor_CreatesUnboundedChannel_SuccessfullyAsync() {
    // Arrange & Act
    var writer = new PerspectiveChannelWriter();

    // Assert
    await Assert.That(writer).IsNotNull();
    await Assert.That(writer.Reader).IsNotNull();
  }

  [Test]
  public async Task WriteAsync_WithValidWork_WritesToChannelAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();
    var work = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    // Act
    await writer.WriteAsync(work);

    // Assert - reader should have the work
    var canRead = writer.Reader.TryRead(out var readWork);
    await Assert.That(canRead).IsTrue();
    await Assert.That(readWork).IsEqualTo(work);
  }

  [Test]
  public async Task TryWrite_WithValidWork_ReturnsTrueAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();
    var work = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    // Act
    var result = writer.TryWrite(work);

    // Assert
    await Assert.That(result).IsTrue();

    // Verify work is in channel
    var canRead = writer.Reader.TryRead(out var readWork);
    await Assert.That(canRead).IsTrue();
    await Assert.That(readWork).IsEqualTo(work);
  }

  [Test]
  public async Task Complete_MarksChannelAsComplete_SuccessfullyAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();

    // Act
    writer.Complete();

    // Assert - reader should complete when drained
    await Assert.That(writer.Reader.Completion.IsCompleted).IsTrue();
  }

  [Test]
  public async Task TryWrite_AfterComplete_ReturnsFalseAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();
    writer.Complete();

    var work = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    // Act
    var result = writer.TryWrite(work);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Reader_SupportsMultipleConcurrentReaders_SuccessfullyAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();
    var work1 = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective1",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };
    var work2 = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective2",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    await writer.WriteAsync(work1);
    await writer.WriteAsync(work2);

    // Act - two concurrent readers
    var reader1Task = Task.Run(async () => await writer.Reader.ReadAsync());
    var reader2Task = Task.Run(async () => await writer.Reader.ReadAsync());

    var results = await Task.WhenAll(reader1Task, reader2Task);

    // Assert - both works should be read
    await Assert.That(results).HasCount().EqualTo(2);
    await Assert.That(results).Contains(work1);
    await Assert.That(results).Contains(work2);
  }

  [Test]
  public async Task WriteAsync_SupportsMultipleConcurrentWriters_SuccessfullyAsync() {
    // Arrange
    var writer = new PerspectiveChannelWriter();
    var work1 = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective1",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };
    var work2 = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective2",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    // Act - two concurrent writers
    var writer1Task = writer.WriteAsync(work1);
    var writer2Task = writer.WriteAsync(work2);

    await Task.WhenAll(writer1Task.AsTask(), writer2Task.AsTask());

    // Assert - both works should be in channel
    var readWork1 = await writer.Reader.ReadAsync();
    var readWork2 = await writer.Reader.ReadAsync();

    await Assert.That(new[] { readWork1, readWork2 }).Contains(work1);
    await Assert.That(new[] { readWork1, readWork2 }).Contains(work2);
  }
}
