// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using MessageId = Whizbang.Core.ValueObjects.MessageId;

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

  /// <summary>Hands the payload back as it is: the receptors in these tests never look at it.</summary>
  private sealed class PassThroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => JsonDocument.Parse(jsonBytes).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  /// <summary>
  /// A receptor at <see cref="LifecycleStage.PostDistributeDetached"/> that holds until released, so the detached work a
  /// flush starts is still running when the host stops.
  /// </summary>
  private sealed class HeldDetachedReceptor : IReceptorInvoker {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _release.TrySetResult();

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostDistributeDetached) {
        Entered.TrySetResult();
        await _release.Task.ConfigureAwait(false);
      }
    }
  }

  /// <summary>Every log entry at Information or above, with a signal for the shutdown waiting on detached work.</summary>
  private sealed class ShutdownLog : ILoggerProvider {
    public ConcurrentQueue<string> Errors { get; } = new();
    public TaskCompletionSource WaitingForDetachedWork { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(ShutdownLog log) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        var message = formatter(state, exception);
        if (logLevel >= LogLevel.Error) {
          log.Errors.Enqueue($"{message} {exception?.Message}");
        } else if (message.Contains("detached lifecycle", StringComparison.OrdinalIgnoreCase)) {
          log.WaitingForDetachedWork.TrySetResult();
        }
      }
    }
  }

  private static OutboxMessage _outboxMessage() {
    var messageId = MessageId.New();
    return new OutboxMessage {
      MessageId = messageId.Value,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }

  /// <summary>
  /// The outbox flush starts its <see cref="LifecycleStage.PostDistributeDetached"/> receptors in the background. A host
  /// that disposed its container while they still ran logged them failing against a disposed service provider, so the
  /// stop waits for them first.
  /// </summary>
  [Test]
  public async Task DetachedLifecycleWorkStillRunning_IsWaitedFor_BeforeTheHostFinishesStoppingAsync() {
    var log = new ShutdownLog();
    var receptor = new HeldDetachedReceptor();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging(logging => logging.AddProvider(log));
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();
    services.AddSingleton<IWorkCoordinator>(new NoOpWorkCoordinator());
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddSingleton<ILifecycleMessageDeserializer>(new PassThroughDeserializer());
    services.AddSingleton<IReceptorInvoker>(receptor);
    var registration = services.Single(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType == typeof(BatchStrategyShutdownFlush));
    var provider = services.BuildServiceProvider();
    var flush = (IHostedLifecycleService)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!);

    // The immediate batcher flushes on append: the store is done and the detached stage is running when this returns.
    await provider.GetRequiredService<IOutboxBatchStrategy>().AppendAsync(_outboxMessage());
    await receptor.Entered.Task;

    var stopped = flush.StoppedAsync(CancellationToken.None);
    await Task.WhenAny(stopped, log.WaitingForDetachedWork.Task);

    await Assert.That(stopped.IsCompleted).IsFalse()
      .Because("the host disposes its container once the stop returns, and the detached receptors still need it");

    receptor.Release();
    await stopped;
    await provider.DisposeAsync();

    await Assert.That(log.Errors).IsEmpty()
      .Because("detached lifecycle work finishes before the service provider it runs against is disposed");
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
