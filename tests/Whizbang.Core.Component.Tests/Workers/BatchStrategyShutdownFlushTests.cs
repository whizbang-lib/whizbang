// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1333: the producer-side batchers are flushed while the host stops, once every hosted service has stopped and
/// before the container is disposed. Flushed only at disposal, the last batch's flush ran against a service provider
/// that was already gone, so its lifecycle receptors failed and its store could be lost.
/// </summary>
/// <remarks>
/// The host calls <see cref="IHostedLifecycleService.StoppedAsync"/> on every lifecycle service after every
/// <see cref="IHostedService.StopAsync"/> has returned, and disposes the container only after that. The tests find the
/// worker pipeline's registration and drive that seam directly, as the host does.
/// </remarks>
public class BatchStrategyShutdownFlushTests {

  private sealed class Recorder(string name, ConcurrentQueue<string> flushes, Exception? failWith = null)
      : IInboxBatchStrategy, IOutboxBatchStrategy {
    public ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
      flushes.Enqueue(name);
      return failWith is null ? Task.CompletedTask : Task.FromException(failWith);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class ErrorLog : ILoggerProvider {
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(Entries);
    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<string> entries) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (IsEnabled(logLevel)) {
          entries.Enqueue($"{formatter(state, exception)} {exception?.Message}");
        }
      }
    }
  }

  private static (ServiceProvider Services, IHostedLifecycleService Flush) _compose(
      ConcurrentQueue<string> flushes, ErrorLog errors, Exception? inboxFails = null) {
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddProvider(errors));
    services.AddWhizbangWorkers();
    services.Replace(ServiceDescriptor.Singleton<IInboxBatchStrategy>(new Recorder("inbox", flushes, inboxFails)));
    services.Replace(ServiceDescriptor.Singleton<IOutboxBatchStrategy>(new Recorder("outbox", flushes)));
    // The pipeline's registration, built as the host builds it; the other hosted services need a real host.
    var registration = services.Single(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType == typeof(BatchStrategyShutdownFlush));
    var provider = services.BuildServiceProvider();
    return (provider, (IHostedLifecycleService)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!));
  }

  [Test]
  public async Task OnceTheHostHasStopped_BothBatchersAreFlushed_InboxFirstAsync() {
    var flushes = new ConcurrentQueue<string>();
    var (services, flush) = _compose(flushes, new ErrorLog());
    await using var _ = services;

    await flush.StoppedAsync(CancellationToken.None);

    await Assert.That(string.Join(",", flushes)).IsEqualTo("inbox,outbox")
      .Because("received messages are stored before the outbox's last batch, and both while the provider is alive");
  }

  [Test]
  public async Task AFailedInboxFlush_IsLogged_AndTheOutboxIsStillFlushedAsync() {
    var flushes = new ConcurrentQueue<string>();
    var errors = new ErrorLog();
    var (services, flush) = _compose(flushes, errors, new InvalidOperationException("inbox store unavailable"));
    await using var _ = services;

    await flush.StoppedAsync(CancellationToken.None);

    await Assert.That(string.Join(",", flushes)).IsEqualTo("inbox,outbox");
    await Assert.That(errors.Entries.Any(e => e.Contains("inbox", StringComparison.OrdinalIgnoreCase)
                                          && e.Contains("inbox store unavailable", StringComparison.Ordinal))).IsTrue()
      .Because("a flush that fails at shutdown says which batcher failed and why");
  }

  [Test]
  public async Task WithNoBatchersRegistered_StoppingDoesNothingAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();
    var flush = new BatchStrategyShutdownFlush(services, Microsoft.Extensions.Logging.Abstractions.NullLogger<BatchStrategyShutdownFlush>.Instance);

    await flush.StartingAsync(CancellationToken.None);
    await flush.StartAsync(CancellationToken.None);
    await flush.StartedAsync(CancellationToken.None);
    await flush.StoppingAsync(CancellationToken.None);
    await flush.StopAsync(CancellationToken.None);

    await Assert.That(async () => await flush.StoppedAsync(CancellationToken.None)).ThrowsNothing()
      .Because("a host without the worker pipeline's batchers has nothing to flush");
  }

  [Test]
  public async Task ACanceledFlush_IsNotSwallowedAsync() {
    var flushes = new ConcurrentQueue<string>();
    var (services, flush) = _compose(flushes, new ErrorLog(), new OperationCanceledException());
    await using var _ = services;

    await Assert.That(async () => await flush.StoppedAsync(CancellationToken.None)).Throws<OperationCanceledException>()
      .Because("a shutdown that ran out of time is the host's to see, not a batcher failure to log");
  }
}
