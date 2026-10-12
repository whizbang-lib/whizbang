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
/// inbox goes first, so received messages are stored before the outbox's last batch.
/// </remarks>
/// <docs>messaging/outbox-pattern</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchStrategyShutdownFlushTests.cs</tests>
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
  }

  private async Task _flushAsync(string batcher, Func<CancellationToken, Task> flushAndStop, CancellationToken cancellationToken) {
    try {
      await flushAndStop(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // One batcher's failure must not keep the other from flushing.
      LogFlushFailed(logger, ex, batcher);
    }
  }

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Flushing the {Batcher} batcher at shutdown failed; its last batch may not have been stored")]
  static partial void LogFlushFailed(ILogger logger, Exception ex, string batcher);
}
