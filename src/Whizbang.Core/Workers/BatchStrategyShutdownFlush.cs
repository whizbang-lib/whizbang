// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Flushes the producer-side batchers while the host stops: once every hosted service has stopped, so nothing appends
/// any more, and before the container is disposed, so the flush callbacks still have the services they resolve.
/// </summary>
/// <remarks>
/// Without it the batchers flushed only when the container disposed them, and the last batch's flush ran against a
/// service provider that was already gone: its lifecycle receptors failed, and its store could be lost (#1333). The
/// inbox goes first, so received messages are stored before the outbox's last batch. Then it waits for the detached
/// lifecycle stages those flushes (and earlier ones) started in the background, which would otherwise run against the
/// disposed provider too. Every entry point registers it (<c>TryAddWhizbangDefaults</c>), so a host without the worker
/// pipeline still stops its work coordinator strategies and waits for the dispatcher's and the receptor invoker's
/// detached work (#1721).
/// </remarks>
/// <docs>messaging/outbox-pattern</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchStrategyShutdownFlushTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Shutdown/DetachedStagesShutdownTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Shutdown/StrategyFlushShutdownTests.cs</tests>
public sealed partial class BatchStrategyShutdownFlush(
    IServiceProvider services,
    ILogger<BatchStrategyShutdownFlush> logger) : IHostedLifecycleService {

  /// <inheritdoc />
  public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public async Task StoppedAsync(CancellationToken cancellationToken) {
    if (services.GetService<IInboxBatchStrategy>() is { } inbox) {
      await _flushAsync("inbox", inbox.FlushAndStopAsync, cancellationToken).ConfigureAwait(false);
    }
    if (services.GetService<IOutboxBatchStrategy>() is { } outbox) {
      await _flushAsync("outbox", outbox.FlushAndStopAsync, cancellationToken).ConfigureAwait(false);
    }
    // The flushes start their detached lifecycle stages in the background; once this returns the host disposes the
    // container they create their scopes from, so they finish first. The producers that start more of that work on a
    // timer (the interval and batch strategies) are stopped and flushed before the wait, so none starts after it (#1721).
    if (services.GetService<DetachedLifecycleWork>() is { } detached) {
      await _flushAsync("work coordinator strategy", detached.StopProducersAsync, cancellationToken).ConfigureAwait(false);
      var inFlight = detached.InFlightCount;
      if (inFlight > 0) {
        LogWaitingForDetachedLifecycleWork(logger, inFlight);
      }
      await detached.DrainAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  private async Task _flushAsync(string batcher, Func<CancellationToken, Task> flushAndStop, CancellationToken cancellationToken) {
    try {
      await flushAndStop(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // One batcher's failure must not keep the other from flushing.
      LogFlushFailed(logger, ex, batcher);
    }
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Waiting for {Count} detached lifecycle invocation(s) to finish before the service provider is disposed")]
  static partial void LogWaitingForDetachedLifecycleWork(ILogger logger, int count);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Flushing the {Batcher} batcher at shutdown failed; its last batch may not have been stored")]
  static partial void LogFlushFailed(ILogger logger, Exception ex, string batcher);
}
