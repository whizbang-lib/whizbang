using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Sequencing;

namespace Whizbang.Sequencing.Tests;

/// <summary>
/// Contract tests that all ISequenceProvider implementations must pass.
/// These tests define the required behavior for sequence providers.
/// </summary>
[Category("Sequencing")]
public abstract class SequenceProviderContractTests {
  /// <summary>
  /// Factory method that derived classes must implement to provide their specific implementation.
  /// </summary>
  protected abstract ISequenceProvider CreateProvider();

  [Test]
  public async Task GetNextAsync_FirstCall_ShouldReturnZeroAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";

    // Act
    var sequence = await provider.GetNextAsync(streamKey);

    // Assert
    await Assert.That(sequence).IsEqualTo(0L);
  }

  [Test]
  public async Task GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";

    // Act
    var seq1 = await provider.GetNextAsync(streamKey);
    var seq2 = await provider.GetNextAsync(streamKey);
    var seq3 = await provider.GetNextAsync(streamKey);

    // Assert
    await Assert.That(seq1).IsEqualTo(0L);
    await Assert.That(seq2).IsEqualTo(1L);
    await Assert.That(seq3).IsEqualTo(2L);
  }

  [Test]
  public async Task GetNextAsync_DifferentStreamKeys_ShouldMaintainSeparateSequencesAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey1 = "stream-1";
    var streamKey2 = "stream-2";

    // Act
    var seq1_1 = await provider.GetNextAsync(streamKey1);
    var seq2_1 = await provider.GetNextAsync(streamKey2);
    var seq1_2 = await provider.GetNextAsync(streamKey1);
    var seq2_2 = await provider.GetNextAsync(streamKey2);

    // Assert - Each stream has independent sequences
    await Assert.That(seq1_1).IsEqualTo(0L);
    await Assert.That(seq2_1).IsEqualTo(0L);
    await Assert.That(seq1_2).IsEqualTo(1L);
    await Assert.That(seq2_2).IsEqualTo(1L);
  }

  [Test]
  public async Task GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";

    // Act
    var current = await provider.GetCurrentAsync(streamKey);

    // Assert - Stream not yet initialized
    await Assert.That(current).IsEqualTo(-1L);
  }

  [Test]
  public async Task GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    await provider.GetNextAsync(streamKey); // 0
    await provider.GetNextAsync(streamKey); // 1
    await provider.GetNextAsync(streamKey); // 2

    // Act
    var current = await provider.GetCurrentAsync(streamKey);

    // Assert - Should return last issued (2), not next (3)
    await Assert.That(current).IsEqualTo(2L);
  }

  [Test]
  public async Task GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    await provider.GetNextAsync(streamKey); // 0

    // Act
    var current1 = await provider.GetCurrentAsync(streamKey);
    var current2 = await provider.GetCurrentAsync(streamKey);
    var current3 = await provider.GetCurrentAsync(streamKey);

    // Assert - Multiple calls should not increment
    await Assert.That(current1).IsEqualTo(0L);
    await Assert.That(current2).IsEqualTo(0L);
    await Assert.That(current3).IsEqualTo(0L);
  }

  [Test]
  public async Task ResetAsync_WithDefaultValue_ShouldResetToZeroAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    await provider.GetNextAsync(streamKey); // 0
    await provider.GetNextAsync(streamKey); // 1
    await provider.GetNextAsync(streamKey); // 2

    // Act
    await provider.ResetAsync(streamKey);
    var next = await provider.GetNextAsync(streamKey);

    // Assert - Should start over at 0
    await Assert.That(next).IsEqualTo(0L);
  }

  [Test]
  public async Task ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    await provider.GetNextAsync(streamKey); // 0

    // Act
    await provider.ResetAsync(streamKey, 100);
    var next = await provider.GetNextAsync(streamKey);

    // Assert - Should continue from 100
    await Assert.That(next).IsEqualTo(100L);
  }

  [Test]
  public async Task ResetAsync_MultipleTimes_ShouldAlwaysResetAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";

    // Act & Assert
    await provider.GetNextAsync(streamKey);
    await provider.ResetAsync(streamKey, 10);
    await Assert.That(await provider.GetNextAsync(streamKey)).IsEqualTo(10L);

    await provider.ResetAsync(streamKey, 50);
    await Assert.That(await provider.GetNextAsync(streamKey)).IsEqualTo(50L);

    await provider.ResetAsync(streamKey);
    await Assert.That(await provider.GetNextAsync(streamKey)).IsEqualTo(0L);
  }

  [Test]
  public async Task GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    var concurrency = 100;

    // Act - Fire 100 concurrent GetNext calls
    var tasks = Enumerable.Range(0, concurrency)
        .Select(_ => provider.GetNextAsync(streamKey))
        .ToArray();

    var sequences = await Task.WhenAll(tasks);

    // Assert - All sequences should be unique and in range [0..99]
    await Assert.That(sequences.Distinct().Count()).IsEqualTo(concurrency);
    await Assert.That(sequences.Min()).IsEqualTo(0L);
    await Assert.That(sequences.Max()).IsEqualTo((long)concurrency - 1);
  }

  [Test]
  public async Task GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    var count = 1000;

    // Act - Sequential calls
    var sequences = new long[count];
    for (int i = 0; i < count; i++) {
      sequences[i] = await provider.GetNextAsync(streamKey);
    }

    // Assert - Should be exactly [0, 1, 2, ..., 999]
    var expected = Enumerable.Range(0, count).Select(i => (long)i).ToArray();
    await Assert.That(sequences).Count().IsEqualTo(expected.Length);
    for (int i = 0; i < sequences.Length; i++) {
      await Assert.That(sequences[i]).IsEqualTo(expected[i]);
    }
  }

  [Test]
  public async Task CancellationToken_WhenCancelled_ShouldThrowAsync() {
    // Arrange
    var provider = CreateProvider();
    var streamKey = "test-stream";
    var cts = new CancellationTokenSource();
    cts.Cancel(); // Already cancelled

    // Act & Assert
    await Assert.That(async () => await provider.GetNextAsync(streamKey, cts.Token))
        .ThrowsExactly<OperationCanceledException>();

    await Assert.That(async () => await provider.GetCurrentAsync(streamKey, cts.Token))
        .ThrowsExactly<OperationCanceledException>();

    await Assert.That(async () => await provider.ResetAsync(streamKey, 0, cts.Token))
        .ThrowsExactly<OperationCanceledException>();
  }
}
