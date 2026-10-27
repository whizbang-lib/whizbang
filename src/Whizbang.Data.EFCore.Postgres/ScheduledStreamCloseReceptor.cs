// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// A1 — the built-in bridge that turns a fired <see cref="ScheduledStreamClose"/> occurrence into an
/// <see cref="IStreamPrefixTruncator.TruncateAsync"/> call, so an F2 recurring schedule ("close the month") drives a
/// stream close. Lives in the driver assembly (not <c>Whizbang.Core</c>) because a receptor in Core would
/// make the receptor-discovery generator emit dispatcher registrations that collide with every consumer's;
/// and scheduled close needs the Postgres temporal engine anyway. Held as a singleton in
/// <c>IReceptorRegistry</c> (runtime-registered), so it resolves the scoped <see cref="IStreamPrefixTruncator"/> fresh
/// per invocation. Inert if no <see cref="IStreamPrefixTruncator"/> is registered.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed partial class ScheduledStreamCloseReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledStreamCloseReceptor> logger) : IReceptor<ScheduledStreamClose> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(ScheduledStreamClose message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var truncator = scope.ServiceProvider.GetService<IStreamPrefixTruncator>();
    if (truncator is null) {
      LogNoTruncator(logger, message.StreamId);
      return;
    }
    var result = await truncator
      .TruncateAsync(message.StreamId, message.ThroughVersion, message.Archive, cancellationToken)
      .ConfigureAwait(false);
    LogScheduledClose(logger, message.StreamId, message.ThroughVersion, message.Archive, result.Status, result.EventsTruncated);
  }

  [LoggerMessage(EventId = 45, Level = LogLevel.Warning,
    Message = "ScheduledStreamClose fired for stream {StreamId} but no IStreamPrefixTruncator is registered; ignored")]
  static partial void LogNoTruncator(ILogger logger, Guid streamId);

  [LoggerMessage(EventId = 46, Level = LogLevel.Information,
    Message = "Scheduled close of stream {StreamId} through version {ThroughVersion} (archive={Archive}): {Status}, {EventsTruncated} truncated")]
  static partial void LogScheduledClose(ILogger logger, Guid streamId, long throughVersion, bool archive, string status, long eventsTruncated);
}
