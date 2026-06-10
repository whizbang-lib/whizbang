namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// What completion the dispatcher's new W4 <c>LocalInvokeAndSyncAsync</c>
/// overload waits for before returning. Selected by the caller per-dispatch;
/// there is no implicit default sync mode at the framework level — every
/// callsite makes its read-after-write expectation explicit.
/// </summary>
/// <remarks>
/// Introduced as part of W4 (dispatcher API cleanup). Replaces the older
/// <c>TimeSpan? timeout = null</c> shaped overloads with a
/// <see cref="System.Threading.CancellationToken"/>-only contract, since
/// perspective health is an observability concern (not a per-call defense)
/// and timing-based code at the callsite tends to hide signal-based
/// correctness.
/// </remarks>
/// <docs>fundamentals/dispatcher/sync-mode</docs>
public enum SyncMode {
  /// <summary>
  /// Wait until events emitted by the dispatched message are durably
  /// written + the local stream version has caught up. Does NOT wait for
  /// perspectives. Fastest return; caller is responsible for any
  /// read-after-write semantics needed afterwards.
  /// </summary>
  /// <remarks>
  /// Use this when the caller doesn't read from any local perspective in
  /// the same request, or when perspective-sync latency is unacceptable
  /// for a hot bulk path.
  /// </remarks>
  StreamOnly,

  /// <summary>
  /// Wait until every locally-registered perspective that subscribes to
  /// the emitted event types has finished projecting. Read-after-write is
  /// guaranteed against any local read model when the dispatch returns.
  /// </summary>
  /// <remarks>
  /// This is the read-after-write CQRS default. Cross-service perspectives
  /// (running in another process) are NOT awaited — only the current
  /// process's perspective registry.
  /// </remarks>
  AllProjections,
}
