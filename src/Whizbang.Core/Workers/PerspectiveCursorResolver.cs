using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default <see cref="IPerspectiveCursorResolver"/>. Combines an in-process
/// <see cref="PerspectiveCursorCache"/> with persisted-cursor reads against
/// <see cref="IWorkCoordinator.GetPerspectiveCursorAsync"/>.
/// </summary>
/// <remarks>
/// Cold-lookup behavior is the load-bearing semantic: when the cache holds
/// nothing for <c>(streamId, perspectiveName)</c>, the resolver reads the
/// persisted cursor + commit_sequence and warms the cache so subsequent calls
/// (and any other component reading <see cref="PerspectiveCursorCache.TryGet"/>
/// directly) see the same truth source the runner's idempotency filter uses.
/// </remarks>
/// <docs>fundamentals/perspectives/cursor-inversion</docs>
public sealed partial class PerspectiveCursorResolver : IPerspectiveCursorResolver {
  private readonly PerspectiveCursorCache _cache;
  private readonly IWorkCoordinator _coordinator;
  private readonly ILogger<PerspectiveCursorResolver> _logger;

  /// <summary>Initializes a new instance.</summary>
  public PerspectiveCursorResolver(
      PerspectiveCursorCache cache,
      IWorkCoordinator coordinator,
      ILogger<PerspectiveCursorResolver> logger) {
    _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public async Task<(Guid? LastEventId, long? LastCommitSequence)> GetAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    // Warm-path: both halves of the cache present → return without I/O.
    var hasEventId = _cache.TryGet(streamId, perspectiveName, out var cachedEventId);
    var hasCommitSeq = _cache.TryGetCommitSequence(streamId, perspectiveName, out var cachedCommitSeq);
    if (hasEventId && hasCommitSeq) {
      return (cachedEventId, cachedCommitSeq);
    }

    // Cold-path: read persisted state. The coordinator returns null when the
    // perspective has never advanced for this stream — that's a valid first-ever
    // scan, not an error.
    var info = await _coordinator.GetPerspectiveCursorAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false);
    if (info is null) {
      // No persisted cursor — do NOT poison the cache with nulls. The next
      // successful apply will Set() the real values; until then, callers see
      // "no cursor" consistently.
      return (null, null);
    }

    // Warm the cache so subsequent in-process callers (inversion detector +
    // any direct PerspectiveCursorCache.TryGet callers) see the same values.
    _cache.Set(streamId, perspectiveName, info.LastEventId);
    _cache.SetCommitSequence(streamId, perspectiveName, info.LastCommitSequence);

    LogCacheHydratedFromPersisted(_logger, streamId, perspectiveName,
      info.LastEventId, info.LastCommitSequence);

    return (info.LastEventId, info.LastCommitSequence);
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Debug,
    Message = "PerspectiveCursorCache hydrated from persisted cursor for {StreamId}/{PerspectiveName}: last_event_id={LastEventId}, last_commit_sequence={LastCommitSequence}")]
  private static partial void LogCacheHydratedFromPersisted(
    ILogger logger,
    Guid streamId,
    string perspectiveName,
    Guid? lastEventId,
    long? lastCommitSequence);
}
