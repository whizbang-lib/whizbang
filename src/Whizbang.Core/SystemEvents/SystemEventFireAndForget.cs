// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Emits a system event without making the caller wait for it, and never loses one silently.
/// </summary>
/// <remarks>
/// <para>
/// A security record (a denied read, for example) is emitted fire-and-forget so the work it describes
/// is never slowed or failed by its own audit. Discarding the emit's task, as the call sites once did,
/// also discarded its failure: when the store refused the record, nothing said so. Every
/// fire-and-forget emit goes through <see cref="Emit"/> instead, which observes the task and, when the
/// emit fails, logs a warning naming what was lost (the event type, its tenant and the stream it was
/// meant for) and counts it on <c>whizbang.system_events.emit_failures</c>, tagged with the event type.
/// </para>
/// <para>
/// The caller is never blocked beyond the emit's own synchronous part (exactly as before) and never
/// faulted: a synchronous throw is caught the same way as a failed task.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#fire-and-forget-emits</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventFireAndForgetTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Lenses/ScopedLensFactoryEmitFailureTests.cs</tests>
public static partial class SystemEventFireAndForget {
  /// <summary>
  /// Starts the emit and returns without waiting for it. A failure is logged and counted, never thrown.
  /// </summary>
  /// <typeparam name="TSystemEvent">The system event type.</typeparam>
  /// <param name="emitter">The emitter to store the event through.</param>
  /// <param name="systemEvent">The event to emit.</param>
  /// <param name="tenantId">The tenant the event concerns, named in the log when the emit fails; null when none.</param>
  /// <param name="logger">Where a failed emit is reported.</param>
  /// <param name="metrics">Where a failed emit is counted; null counts nothing (the log still names the loss).</param>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventFireAndForgetTests.cs:AFailedEmit_IsLoggedWithWhatWasLost_AndCountedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventFireAndForgetTests.cs:TheCaller_IsNotBlockedWhileTheEmitIsStillRunningAsync</tests>
  public static void Emit<TSystemEvent>(
      ISystemEventEmitter emitter,
      TSystemEvent systemEvent,
      string? tenantId,
      ILogger logger,
      EventCategoryMetrics? metrics) where TSystemEvent : ISystemEvent {
    ArgumentNullException.ThrowIfNull(emitter);
    ArgumentNullException.ThrowIfNull(systemEvent);
    ArgumentNullException.ThrowIfNull(logger);
    _ = _observeAsync(emitter, systemEvent, tenantId, logger, metrics);
  }

  private static async Task _observeAsync<TSystemEvent>(
      ISystemEventEmitter emitter,
      TSystemEvent systemEvent,
      string? tenantId,
      ILogger logger,
      EventCategoryMetrics? metrics) where TSystemEvent : ISystemEvent {
    try {
      await emitter.EmitAsync(systemEvent).ConfigureAwait(false);
    } catch (Exception ex) {
      // Nothing above this frame observes the task, so this catch is the only place the loss can be
      // reported. It must not rethrow: an unobserved fault would surface nowhere useful.
      var eventType = typeof(TSystemEvent).Name;
      LogSystemEventLost(logger, ex, eventType, tenantId, SystemEventStreams.StreamFor(systemEvent));
      metrics?.SystemEventEmitFailures.Add(1, new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventType));
    }
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "A {EventType} system event for tenant '{TenantId}' was not stored on stream {StreamId}: the fire-and-forget emit failed and the record is lost."
  )]
  private static partial void LogSystemEventLost(ILogger logger, Exception exception, string eventType, string? tenantId, Guid streamId);
}
