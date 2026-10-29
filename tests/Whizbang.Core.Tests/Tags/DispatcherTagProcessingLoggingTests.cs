// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// The dispatcher's tag-processing step runs after every successful dispatch, so its diagnostic logging must cost
/// nothing while debug logging is off (#1756). Each path is measured on the current thread after a warm-up call,
/// with a processor that allocates nothing, so the only bytes counted are the step's own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/messages/message-tags#processing</docs>
[NotInParallel("AllocationMeasurement")]
public class DispatcherTagProcessingLoggingTests {
  private const int ITERATIONS = 1_000;

  public record LoggingProbeCommand(Guid Id);

  /// <summary>Counts invocations and allocates nothing, so the measurement sees only the dispatcher's own cost.</summary>
  private sealed class CountingTagProcessor : IMessageTagProcessor {
    public int InvocationCount { get; private set; }

    public ValueTask ProcessTagsAsync(object message, Type messageType, LifecycleStage stage,
        IScopeContext? scope = null, CancellationToken ct = default) {
      InvocationCount++;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task TagProcessingDisabled_WithDebugOff_AllocatesNothingAsync() {
    var dispatcher = _createDispatcher(new CountingTagProcessor(), LogLevel.Information, options => options.EnableTagProcessing = false);

    var allocated = _measure(dispatcher);

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("the step runs after every dispatch; with debug off its log calls must not build argument arrays");
  }

  [Test]
  public async Task LifecycleStageMode_WithDebugOff_AllocatesNothingAsync() {
    var dispatcher = _createDispatcher(new CountingTagProcessor(), LogLevel.Information, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;
    });

    var allocated = _measure(dispatcher);

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("logging the processing mode must not box the enum while debug is off");
  }

  [Test]
  public async Task NoProcessorRegistered_WithDebugOff_AllocatesNothingAsync() {
    var dispatcher = _createDispatcher(processor: null, LogLevel.Information, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });

    var allocated = _measure(dispatcher);

    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  public async Task ProcessorInvoked_WithDebugOff_AllocatesNothingAsync() {
    var processor = new CountingTagProcessor();
    var dispatcher = _createDispatcher(processor, LogLevel.Information, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });

    var allocated = _measure(dispatcher);

    await Assert.That(processor.InvocationCount).IsEqualTo(ITERATIONS + 1)
      .Because("the measured path must be the one that reaches the processor, warm-up included");
    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  public async Task TagProcessingDisabled_WithDebugOn_LogsTheEntryAndTheSkipAsync() {
    var capture = new CapturingLoggerProvider();
    var dispatcher = _createDispatcher(new CountingTagProcessor(), LogLevel.Debug, options => options.EnableTagProcessing = false, capture);

    await dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand));

    await Assert.That(capture.Messages).Contains($"ProcessTagsIfEnabledAsync called for {nameof(LoggingProbeCommand)}");
    await Assert.That(capture.Messages).Contains("Tag processing is DISABLED - returning early");
  }

  [Test]
  public async Task LifecycleStageMode_WithDebugOn_LogsTheModeAsync() {
    var capture = new CapturingLoggerProvider();
    var dispatcher = _createDispatcher(new CountingTagProcessor(), LogLevel.Debug, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;
    }, capture);

    await dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand));

    await Assert.That(capture.Messages)
      .Contains("TagProcessingMode is AsLifecycleStage, not AfterReceptorCompletion - returning early");
  }

  [Test]
  public async Task NoProcessorRegistered_WithDebugOn_LogsTheSkipAsync() {
    var capture = new CapturingLoggerProvider();
    var dispatcher = _createDispatcher(processor: null, LogLevel.Debug, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    }, capture);

    await dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand));

    await Assert.That(capture.Messages).Contains("No IMessageTagProcessor registered - returning early");
  }

  [Test]
  public async Task ProcessorInvoked_WithDebugOn_LogsBeforeAndAfterAsync() {
    var capture = new CapturingLoggerProvider();
    var dispatcher = _createDispatcher(new CountingTagProcessor(), LogLevel.Debug, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    }, capture);

    await dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand));

    await Assert.That(capture.Messages).Contains("Calling ProcessTagsAsync at AfterReceptorCompletion...");
    await Assert.That(capture.Messages).Contains("ProcessTagsAsync completed");
  }

  /// <summary>
  /// The step completes synchronously only when the processor did; a processor that finishes later is awaited, so
  /// the step does not report completion before the processor has.
  /// </summary>
  [Test]
  public async Task ProcessorCompletingLater_IsAwaitedBeforeTheStepCompletesAsync() {
    var capture = new CapturingLoggerProvider();
    var processor = new DeferredTagProcessor();
    var dispatcher = _createDispatcher(processor, LogLevel.Debug, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    }, capture);

    var step = dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand)).AsTask();

    await Assert.That(step.IsCompleted).IsFalse()
      .Because("the processor has not finished, so neither has the step");
    await Assert.That(capture.Messages).DoesNotContain("ProcessTagsAsync completed");

    processor.Finish();
    await step;

    await Assert.That(capture.Messages).Contains("ProcessTagsAsync completed");
  }

  /// <summary>A processor whose processing faulted reaches the caller with its own exception, not a wrapper.</summary>
  [Test]
  public async Task ProcessorFaulted_ReachesTheCallerAsync() {
    var dispatcher = _createDispatcher(new FaultedTagProcessor(), LogLevel.Information, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });

    InvalidOperationException? caught = null;
    try {
      await dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand));
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).IsEqualTo("tag hook failed");
  }

  /// <summary>A processor that fails after the step has returned also reaches the caller with its own exception.</summary>
  [Test]
  public async Task ProcessorFailingLater_ReachesTheCallerAsync() {
    var processor = new DeferredTagProcessor();
    var dispatcher = _createDispatcher(processor, LogLevel.Information, options => {
      options.EnableTagProcessing = true;
      options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
    });

    var step = dispatcher.ProcessTagsIfEnabledAsync(new LoggingProbeCommand(Guid.CreateVersion7()), typeof(LoggingProbeCommand)).AsTask();
    processor.Fail(new InvalidOperationException("tag hook failed later"));

    InvalidOperationException? caught = null;
    try {
      await step;
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).IsEqualTo("tag hook failed later");
  }

  /// <summary>Returns processing that has already failed.</summary>
  private sealed class FaultedTagProcessor : IMessageTagProcessor {
    public ValueTask ProcessTagsAsync(object message, Type messageType, LifecycleStage stage,
        IScopeContext? scope = null, CancellationToken ct = default) =>
      ValueTask.FromException(new InvalidOperationException("tag hook failed"));
  }

  /// <summary>Returns a processing task the test completes, so the step's asynchronous path runs deterministically.</summary>
  private sealed class DeferredTagProcessor : IMessageTagProcessor {
    private readonly TaskCompletionSource _processing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Finish() => _processing.SetResult();

    public void Fail(Exception exception) => _processing.SetException(exception);

    public ValueTask ProcessTagsAsync(object message, Type messageType, LifecycleStage stage,
        IScopeContext? scope = null, CancellationToken ct = default) => new(_processing.Task);
  }

  /// <summary>
  /// Runs the step once to warm it (the logger is resolved lazily on first use), then measures the bytes the
  /// current thread allocates across <see cref="ITERATIONS"/> calls. Every call must complete synchronously, so
  /// the whole loop stays on this thread.
  /// </summary>
  private static long _measure(global::Whizbang.Core.Dispatcher dispatcher) {
    var message = new LoggingProbeCommand(Guid.CreateVersion7());
    var messageType = typeof(LoggingProbeCommand);
    _runSynchronously(dispatcher.ProcessTagsIfEnabledAsync(message, messageType));

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _runSynchronously(dispatcher.ProcessTagsIfEnabledAsync(message, messageType));
    }
    return GC.GetAllocatedBytesForCurrentThread() - before;
  }

  private static void _runSynchronously(ValueTask pending) {
    if (!pending.IsCompletedSuccessfully) {
      throw new InvalidOperationException("The tag-processing step did not complete synchronously; the measurement would leave this thread.");
    }
    pending.GetAwaiter().GetResult();
  }

  private static global::Whizbang.Core.Dispatcher _createDispatcher(
      IMessageTagProcessor? processor,
      LogLevel minimumLevel,
      Action<WhizbangCoreOptions> configure,
      CapturingLoggerProvider? capture = null) {
    var services = new ServiceCollection();
    services.AddLogging(builder => {
      builder.SetMinimumLevel(minimumLevel);
      builder.AddProvider(capture ?? new CapturingLoggerProvider());
    });
    services.AddWhizbangMessageSecurity(options => options.ExemptMessageTypes.Add(typeof(LoggingProbeCommand)));
    services.AddWhizbang(options => configure(options));

    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMessageTagProcessor));
    if (descriptor != null) {
      services.Remove(descriptor);
    }
    if (processor is not null) {
      services.AddSingleton<IMessageTagProcessor>(processor);
    }

    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    serviceProvider.GetService<Whizbang.Core.Workers.ISchemaReadyGate>()?.MarkReady();
    return (global::Whizbang.Core.Dispatcher)serviceProvider.GetRequiredService<IDispatcher>();
  }

  /// <summary>Captures formatted log output; the logging builder's minimum level decides what reaches it.</summary>
  private sealed class CapturingLoggerProvider : ILoggerProvider {
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
    public ILogger CreateLogger(string categoryName) => new Sink(_messages);
    public void Dispose() { }

    private sealed class Sink(List<string> messages) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (messages) { messages.Add(formatter(state, exception)); }
      }
    }
  }
}
