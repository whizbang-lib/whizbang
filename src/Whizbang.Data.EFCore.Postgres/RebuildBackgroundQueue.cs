// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Whole-perspective blue-green rebuilds handed off by the rebuild command's handler, so the handler returns at once and
/// a rebuild's size is not bounded by the inbox handler's budget (#1353). Held in this instance's memory: a queued rebuild
/// is lost if the instance stops, which leaves the live table as it was, since a blue-green rebuild swaps nothing until it
/// completes.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/RebuildCommandRoutingTests.cs:BlueGreen_WithAQueue_IsQueuedAndTheHandlerDoesNotRunItAsync</tests>
internal sealed class RebuildBackgroundQueue {
  private readonly Channel<(string PerspectiveName, RebuildOrigin Origin)> _channel =
    Channel.CreateUnbounded<(string, RebuildOrigin)>();

  /// <summary>How many rebuilds are queued and not yet started.</summary>
  public int Pending => _channel.Reader.Count;

  internal ChannelReader<(string PerspectiveName, RebuildOrigin Origin)> Reader => _channel.Reader;

  /// <summary>Queues a blue-green rebuild of <paramref name="perspectiveName"/>.</summary>
  public void Enqueue(string perspectiveName, RebuildOrigin origin) =>
    // An unbounded channel that is never completed always accepts a write.
    _ = _channel.Writer.TryWrite((perspectiveName, origin));
}

/// <summary>
/// Runs the queued blue-green rebuilds one at a time, each in its own scope, on the host's lifetime. A failure is reported
/// and the next rebuild still runs; a rebuild cut short by shutdown is not a failure.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/RebuildBackgroundWorkerTests.cs:AQueuedRebuild_IsRunByTheWorkerAndReportedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/RebuildBackgroundWorkerTests.cs:ARebuildThatThrows_IsReportedAndTheNextQueuedRebuildStillRunsAsync</tests>
internal sealed partial class RebuildBackgroundWorker : BackgroundService {
  private readonly RebuildBackgroundQueue _queue;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<RebuildBackgroundWorker> _logger;

  public RebuildBackgroundWorker(RebuildBackgroundQueue queue, IServiceScopeFactory scopeFactory, ILogger<RebuildBackgroundWorker> logger) {
    ArgumentNullException.ThrowIfNull(queue);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(logger);
    _queue = queue;
    _scopeFactory = scopeFactory;
    _logger = logger;
  }

  /// <summary>Raised when a queued rebuild returns, successful or not.</summary>
  public event Action<RebuildResult> OnRebuildFinished = static _ => { };

  /// <summary>Raised when a queued rebuild throws, other than by shutdown.</summary>
  public event Action<string, Exception> OnRebuildFailed = static (_, _) => { };

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await foreach (var (perspectiveName, origin) in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
        // ReadAllAsync hands back an item already queued without looking at the token, so a stopped host could otherwise
        // start a rebuild it will not finish.
        stoppingToken.ThrowIfCancellationRequested();
        await _runAsync(perspectiveName, origin, stoppingToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // The host is stopping. A queued or interrupted blue-green rebuild has swapped nothing, so the live table stands.
      LogStopped(_logger, _queue.Pending);
    }
  }

  private async Task _runAsync(string perspectiveName, RebuildOrigin origin, CancellationToken stoppingToken) {
    try {
      var scope = _scopeFactory.CreateAsyncScope();
      await using (scope.ConfigureAwait(false)) {
        var rebuilder = scope.ServiceProvider.GetRequiredService<IPerspectiveRebuilder>();
        var result = await rebuilder.RebuildBlueGreenAsync(perspectiveName, origin, stoppingToken).ConfigureAwait(false);
        LogFinished(_logger, perspectiveName, result.Success, result.StreamsProcessed);
        OnRebuildFinished(result);
      }
    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
      throw;
    } catch (Exception ex) {
      // One failed rebuild is reported and never stops the queue.
      LogFailed(_logger, ex, perspectiveName);
      OnRebuildFailed(perspectiveName, ex);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Background blue-green rebuild of {PerspectiveName} finished: success {Success}, {StreamsProcessed} streams.")]
  static partial void LogFinished(ILogger logger, string perspectiveName, bool success, int streamsProcessed);

  [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Background blue-green rebuild of {PerspectiveName} failed; the live table is unchanged.")]
  static partial void LogFailed(ILogger logger, Exception exception, string perspectiveName);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Background rebuild worker stopped with {Pending} rebuild(s) still queued; none was swapped in.")]
  static partial void LogStopped(ILogger logger, int pending);
}
