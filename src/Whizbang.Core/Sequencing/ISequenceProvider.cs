namespace Whizbang.Core.Sequencing;

/// <summary>
/// Provides monotonically increasing sequence numbers for streams.
/// Implementations must be thread-safe and guarantee no gaps or duplicates.
/// </summary>
public interface ISequenceProvider {
  /// <summary>
  /// Gets the next sequence number for a stream.
  /// First call for a stream returns 0, subsequent calls increment by 1.
  /// Must be monotonically increasing and thread-safe.
  /// </summary>
  /// <param name="streamKey">The stream identifier</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>The next sequence number (0-based)</returns>
  Task<long> GetNextAsync(string streamKey, CancellationToken ct = default);

  /// <summary>
  /// Gets the current sequence number for a stream without incrementing.
  /// Returns -1 if the stream has not been initialized (no GetNext calls yet).
  /// </summary>
  /// <param name="streamKey">The stream identifier</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>The last issued sequence number, or -1 if stream not initialized</returns>
  Task<long> GetCurrentAsync(string streamKey, CancellationToken ct = default);

  /// <summary>
  /// Resets the sequence for a stream to a specific value.
  /// The next GetNext call will return this value.
  /// WARNING: This is dangerous and should only be used for testing or administrative purposes.
  /// Resetting sequences can break ordering guarantees and cause duplicate sequence numbers.
  /// </summary>
  /// <param name="streamKey">The stream identifier</param>
  /// <param name="newValue">The value to reset to (default 0)</param>
  /// <param name="ct">Cancellation token</param>
  Task ResetAsync(string streamKey, long newValue = 0, CancellationToken ct = default);
}
