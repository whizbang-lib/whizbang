namespace Whizbang.Core.Messaging;

/// <summary>
/// The framework's null default for <see cref="IDeadLetterStore"/>: reports <see cref="IDeadLetterStore.IsConfigured"/>
/// false, which is the "no dead-letter queue, rows keep accumulating" behavior a host without a storage driver had.
/// Callers check the flag first and this implementation throws if they do not.
/// </summary>
public sealed class NullDeadLetterStore : IDeadLetterStore, INullDefault {
  private NullDeadLetterStore() { }

  /// <summary>The shared instance.</summary>
  public static NullDeadLetterStore Instance { get; } = new();

  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <inheritdoc />
  public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
      string? errorText, Guid instanceId, string generation, CancellationToken ct = default) =>
    throw new InvalidOperationException("No dead-letter store is registered; a storage driver supplies one. Check IsConfigured before calling.");
}
